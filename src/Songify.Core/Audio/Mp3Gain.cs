namespace Songify.Core.Audio;

/// <summary>
/// Changes an MP3's volume without re-encoding (what mp3gain does): every Layer III granule stores a global gain,
/// a power-of-2^(1/4) scale on its decoded samples, so adding n to it changes the volume by n × 1.505 dB exactly.
/// The audio data itself isn't touched; tags (ID3v2 at the start, ID3v1/APE at the end) are left as they are.
/// </summary>
public static class Mp3Gain
{
    /// <summary>One global-gain step in dB: 20·log10(2^(1/4)).</summary>
    public const double StepDb = 1.50515;

    private static readonly int[] Mpeg1Kbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Kbps = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    private static readonly int[] Mpeg1Rates = [44100, 48000, 32000];

    /// <summary>
    /// Adds <paramref name="steps"/> to every granule's global gain in <paramref name="file"/> (in place). The change
    /// is reduced if a granule's gain would leave 0–255. Returns the steps applied.
    /// </summary>
    /// <exception cref="InvalidDataException">No Layer III frames found (or only free-format ones, which aren't supported).</exception>
    public static int Adjust(byte[] file, int steps)
    {
        var frames = Frames(file);
        if (frames.Count == 0) throw new InvalidDataException("No MP3 audio found in the file.");

        // Granules that code no audio (part2_3_length 0) are silent whatever their gain; they don't limit the change.
        int min = 255, max = 0;
        foreach (var granule in frames.SelectMany(f => f.Granules).Where(g => g.HasAudio))
        {
            var gain = ReadByte(file, granule.GainBit);
            min = Math.Min(min, gain);
            max = Math.Max(max, gain);
        }
        if (min > max) return 0; // only silence
        steps = Math.Clamp(steps, -min, 255 - max);
        if (steps == 0) return 0;

        foreach (var frame in frames)
        {
            foreach (var granule in frame.Granules)
                WriteByte(file, granule.GainBit, Math.Clamp(ReadByte(file, granule.GainBit) + steps, 0, 255));
            if (frame.HasCrc) WriteCrc(file, frame);
        }
        return steps;
    }

    private sealed record Granule(long GainBit, bool HasAudio);

    private sealed record Frame(int Start, int Length, bool HasCrc, int SideInfoLength, Granule[] Granules);

    private readonly record struct Header(int Length, bool Mpeg1, bool HasCrc, bool Mono, int SampleRate);

    /// <summary>The audio frames whose gains can change: Layer III frames, without the Xing/Info/VBRI header frame.</summary>
    private static List<Frame> Frames(byte[] file)
    {
        var frames = new List<Frame>();
        var position = Id3v2Length(file);
        int? sampleRate = null;
        while (position + 4 <= file.Length)
        {
            if (frames.Count > 0 && IsTrailingTag(file, position)) break;
            // A frame counts only if the next one follows it (or the file ends): sync bytes also occur in tags.
            if (ParseHeader(file, position) is not { } header
                || (sampleRate is not null && header.SampleRate != sampleRate)
                || position + header.Length > file.Length
                || !IsFollowed(file, position + header.Length, header.SampleRate))
            {
                position++;
                continue;
            }

            sampleRate ??= header.SampleRate;
            var frame = Describe(file, position, header);
            if (!(frames.Count == 0 && IsInfoFrame(file, frame)))
                frames.Add(frame);
            position += header.Length;
        }
        return frames;
    }

    private static bool IsFollowed(byte[] file, int next, int sampleRate)
    {
        if (next == file.Length || IsTrailingTag(file, next)) return true;
        return ParseHeader(file, next) is { } following && following.SampleRate == sampleRate;
    }

    /// <summary>Tags after the audio: ID3v1, APEv2, Lyrics3, and ID3v2 (which v2.4 allows at the end).</summary>
    private static bool IsTrailingTag(byte[] file, int at) =>
        Matches(file, at, "TAG"u8) || Matches(file, at, "APETAGEX"u8) || Matches(file, at, "LYRICSBEGIN"u8)
        || Matches(file, at, "ID3"u8);

    private static Header? ParseHeader(byte[] file, int at)
    {
        if (at + 4 > file.Length || file[at] != 0xFF || (file[at + 1] & 0xE0) != 0xE0) return null;
        var version = (file[at + 1] >> 3) & 3;   // 0: MPEG 2.5, 1: reserved, 2: MPEG 2, 3: MPEG 1
        var layer = (file[at + 1] >> 1) & 3;     // 1: Layer III
        var bitrateIndex = file[at + 2] >> 4;
        var rateIndex = (file[at + 2] >> 2) & 3;
        // Free-format (bitrate index 0) frames have no length in the header: not supported, rare.
        if (version == 1 || layer != 1 || bitrateIndex is 0 or 15 || rateIndex == 3) return null;

        var mpeg1 = version == 3;
        var sampleRate = Mpeg1Rates[rateIndex] >> (mpeg1 ? 0 : version == 2 ? 1 : 2);
        var padding = (file[at + 2] >> 1) & 1;
        var kbps = (mpeg1 ? Mpeg1Kbps : Mpeg2Kbps)[bitrateIndex];
        var length = (mpeg1 ? 144_000 : 72_000) * kbps / sampleRate + padding;
        return new Header(length, mpeg1, (file[at + 1] & 1) == 0, file[at + 3] >> 6 == 3, sampleRate);
    }

    /// <summary>
    /// Where each granule's 8-bit global gain sits in the side info (ISO 11172-3 / 13818-3): after main_data_begin,
    /// the private bits and (MPEG-1) scfsi, each granule-channel block starts with part2_3_length (12 bits) and
    /// big_values (9), then global_gain.
    /// </summary>
    private static Frame Describe(byte[] file, int start, Header header)
    {
        var channels = header.Mono ? 1 : 2;
        var sideInfo = start + 4 + (header.HasCrc ? 2 : 0);
        var (firstBlock, blockBits, granules, sideInfoLength) = header.Mpeg1
            ? (9 + (header.Mono ? 5 : 3) + 4 * channels, 59, 2, header.Mono ? 17 : 32)
            : (8 + (header.Mono ? 1 : 2), 63, 1, header.Mono ? 9 : 17);

        var list = new Granule[granules * channels];
        for (var i = 0; i < list.Length; i++)
        {
            var block = (long)sideInfo * 8 + firstBlock + i * blockBits;
            var part23Length = ReadByte(file, block) << 4 | ReadByte(file, block + 8) >> 4;
            list[i] = new Granule(block + 21, part23Length != 0);
        }
        return new Frame(start, header.Length, header.HasCrc, sideInfoLength, list);
    }

    private static bool IsInfoFrame(byte[] file, Frame frame)
    {
        var afterSideInfo = frame.Start + 4 + (frame.HasCrc ? 2 : 0) + frame.SideInfoLength;
        return Matches(file, afterSideInfo, "Xing"u8) || Matches(file, afterSideInfo, "Info"u8)
            || Matches(file, frame.Start + 36, "VBRI"u8);
    }

    private static bool Matches(byte[] file, int at, ReadOnlySpan<byte> text) =>
        at >= 0 && at + text.Length <= file.Length && file.AsSpan(at, text.Length).SequenceEqual(text);

    /// <summary>Size of the ID3v2 tag at the start (with its footer), or 0.</summary>
    private static int Id3v2Length(byte[] file)
    {
        if (!Matches(file, 0, "ID3"u8) || file.Length < 10) return 0;
        var size = (file[6] & 0x7F) << 21 | (file[7] & 0x7F) << 14 | (file[8] & 0x7F) << 7 | (file[9] & 0x7F);
        var footer = (file[5] & 0x10) != 0 ? 10 : 0;
        return Math.Min(file.Length, 10 + size + footer);
    }

    private static int ReadByte(byte[] file, long bit)
    {
        var i = (int)(bit / 8);
        var shift = (int)(bit % 8);
        return ((file[i] << 8 | file[i + 1]) >> (8 - shift)) & 0xFF;
    }

    private static void WriteByte(byte[] file, long bit, int value)
    {
        var i = (int)(bit / 8);
        var shift = 8 - (int)(bit % 8);
        var word = file[i] << 8 | file[i + 1];
        word = (word & ~(0xFF << shift)) | (value << shift);
        file[i] = (byte)(word >> 8);
        file[i + 1] = (byte)word;
    }

    /// <summary>
    /// The frame's CRC-16 (polynomial 0x8005, start 0xFFFF) over the last two header bytes and the side info,
    /// stored big-endian after the header.
    /// </summary>
    private static void WriteCrc(byte[] file, Frame frame)
    {
        var crc = Crc16(file.AsSpan(frame.Start + 2, 2), 0xFFFF);
        crc = Crc16(file.AsSpan(frame.Start + 6, frame.SideInfoLength), crc);
        file[frame.Start + 4] = (byte)(crc >> 8);
        file[frame.Start + 5] = (byte)crc;
    }

    internal static ushort Crc16(ReadOnlySpan<byte> data, ushort crc)
    {
        foreach (var b in data)
            for (var i = 7; i >= 0; i--)
            {
                var feedback = ((b >> i) & 1) != 0 ^ (crc & 0x8000) != 0;
                crc <<= 1;
                if (feedback) crc ^= 0x8005;
            }
        return crc;
    }
}
