using System.Text;
using Songify.Core.Audio;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Tools;

namespace Songify.Tests;

public class LevellingTests
{
    private static LevelAnalysis Analysis(double lufs, double peak, LevelMethod method = LevelMethod.Rewrite) =>
        new(new Loudness(lufs, peak), method, []);

    [Fact]
    public void A_loud_song_is_turned_down_to_the_target()
    {
        Assert.Equal(-6.0, VolumeLeveller.PlanGain(Analysis(-8, 0.5), -14));
    }

    [Fact]
    public void A_quiet_song_is_turned_up_only_until_its_peaks_reach_the_ceiling()
    {
        Assert.Equal(6.0, VolumeLeveller.PlanGain(Analysis(-20, -10), -14), 3);   // room for all 6 dB
        Assert.Equal(3.0, VolumeLeveller.PlanGain(Analysis(-20, -4), -14), 3);     // peaks reach -1 dBTP after 3 dB
        Assert.Equal(0, VolumeLeveller.PlanGain(Analysis(-20, 0.2), -14));         // already peaks: left as it is
    }

    [Fact]
    public void A_song_close_to_the_target_is_left_alone()
    {
        Assert.Equal(0, VolumeLeveller.PlanGain(Analysis(-14.4, -3), -14));
        Assert.Equal(-0.6, VolumeLeveller.PlanGain(Analysis(-13.4, -3), -14), 3);
    }

    [Fact]
    public void Mp3_changes_are_whole_steps_and_never_overshoot_upwards()
    {
        var step = Mp3Gain.StepDb;
        Assert.Equal(-4 * step, VolumeLeveller.PlanGain(Analysis(-8, 0, LevelMethod.Mp3Gain), -14), 6);  // -6 → nearest
        Assert.Equal(step, VolumeLeveller.PlanGain(Analysis(-16.9, -20, LevelMethod.Mp3Gain), -14), 6);  // +2.9 → down to 1 step
        Assert.Equal(0, VolumeLeveller.PlanGain(Analysis(-13.4, -3, LevelMethod.Mp3Gain), -14));        // under half a step
    }

    [Fact]
    public void Loudness_is_read_from_ffmpegs_summary()
    {
        const string stderr = """
            [Parsed_ebur128_0 @ 0000] t: 4.9  TARGET:-23 LUFS    M: -27.8 S: -27.8     I: -27.9 LUFS
            [Parsed_ebur128_0 @ 000001ccd6ec7cc0] Summary:

              Integrated loudness:
                I:         -27.8 LUFS
                Threshold: -37.8 LUFS

              Loudness range:
                LRA:         0.0 LU
                Threshold: -47.8 LUFS
                LRA low:   -27.8 LUFS
                LRA high:  -27.8 LUFS

              True peak:
                Peak:        0.7 dBFS
            """;
        Assert.Equal(new Loudness(-27.8, 0.7), VolumeLeveller.ParseLoudness(stderr));
        Assert.Equal(double.NegativeInfinity, VolumeLeveller.ParseLoudness("Summary:\n I: -70.0 LUFS\n Peak: -inf dBFS")!.TruePeakDb);
        Assert.Null(VolumeLeveller.ParseLoudness("Error opening input"));
    }

    private static string Probe(string codec, string container, string? bitRate = null, string? sampleFormat = null)
    {
        var stream = new System.Text.Json.Nodes.JsonObject { ["codec_name"] = codec, ["sample_fmt"] = sampleFormat ?? "fltp" };
        if (bitRate is not null) stream["bit_rate"] = bitRate;
        return new System.Text.Json.Nodes.JsonObject
        {
            ["streams"] = new System.Text.Json.Nodes.JsonArray(stream),
            ["format"] = new System.Text.Json.Nodes.JsonObject { ["format_name"] = container, ["bit_rate"] = "999000" },
        }.ToJsonString();
    }

    [Fact]
    public void Each_format_gets_the_method_that_keeps_most_quality()
    {
        Assert.Equal(LevelMethod.Mp3Gain, VolumeLeveller.MethodFor(".mp3", Probe("mp3", "mp3")).Method);
        Assert.Equal(LevelMethod.OpusHeader, VolumeLeveller.MethodFor(".opus", Probe("opus", "ogg")).Method);
        Assert.Equal(LevelMethod.OpusHeader, VolumeLeveller.MethodFor(".OGG", Probe("opus", "ogg")).Method);
        var flac = VolumeLeveller.MethodFor(".flac", Probe("flac", "flac", sampleFormat: "s16"));
        Assert.Equal(LevelMethod.Rewrite, flac.Method);
        Assert.Equal(["-c:a", "flac", "-sample_fmt", "s16"], flac.CodecArgs);
        Assert.Equal(["-c:a", "flac", "-sample_fmt", "s32"], VolumeLeveller.MethodFor(".flac", Probe("flac", "flac", sampleFormat: "s32")).CodecArgs);
        Assert.Equal(["-c:a", "pcm_s24le"], VolumeLeveller.MethodFor(".wav", Probe("pcm_s24le", "wav")).CodecArgs);
        var aac = VolumeLeveller.MethodFor(".m4a", Probe("aac", "mov,mp4,m4a,3gp,3g2,mj2", "129478"));
        Assert.Equal(LevelMethod.Reencode, aac.Method);
        Assert.Equal(["-c:a", "aac", "-b:a", "128k"], aac.CodecArgs);
        Assert.Equal(["-c:a", "libvorbis", "-b:a", "320k"], VolumeLeveller.MethodFor(".ogg", Probe("vorbis", "ogg", "500000")).CodecArgs);
    }

    [Theory]
    [InlineData(".m4a", "alac", "mov,mp4,m4a,3gp,3g2,mj2", "Can't level alac audio in a M4A file.")]
    [InlineData(".opus", "opus", "matroska,webm", "Can't level opus audio in a OPUS file.")]
    [InlineData(".mp3", "", "mp3", "Can't level this audio in a MP3 file.")]
    public void Other_formats_are_refused_with_a_reason(string extension, string codec, string container, string message)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => VolumeLeveller.MethodFor(extension, Probe(codec, container)));
        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData("REPLAYGAIN_TRACK_GAIN", true)]
    [InlineData("replaygain_album_peak", true)]
    [InlineData("R128_TRACK_GAIN", true)]
    [InlineData("iTunNORM", true)]
    [InlineData("MP3GAIN_UNDO", true)]
    [InlineData("genre", false)]
    public void Gain_tags_are_recognized(string key, bool expected) => Assert.Equal(expected, VolumeLeveller.IsLevelTag(key));

    [Fact]
    public void Mp3_frame_checksum_is_crc16_with_polynomial_8005()
    {
        // CRC-16/CMS check value.
        Assert.Equal(0xAEE7, Mp3Gain.Crc16("123456789"u8, 0xFFFF));
    }

    /// <summary>An MPEG-1 Layer III stereo frame (128 kbps, 44.1 kHz, with CRC) whose four granules have the given gains.</summary>
    private static byte[] Mp3Frame(params int[] gains)
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFA; frame[2] = 0x90; frame[3] = 0x00;
        for (var i = 0; i < 4; i++)
        {
            var block = 6 * 8 + 20 + i * 59;
            SetBits(frame, block, 12, 100);        // part2_3_length: codes audio
            SetBits(frame, block + 21, 8, gains[i]);
        }
        return frame;
    }

    private static void SetBits(byte[] data, int bit, int count, int value)
    {
        for (var i = 0; i < count; i++)
        {
            var b = bit + i;
            var on = (value >> (count - 1 - i) & 1) != 0;
            if (on) data[b / 8] |= (byte)(0x80 >> (b % 8));
            else data[b / 8] &= (byte)~(0x80 >> (b % 8));
        }
    }

    private static int GetBits(byte[] data, int bit, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++) value = value << 1 | (data[(bit + i) / 8] >> (7 - (bit + i) % 8) & 1);
        return value;
    }

    [Fact]
    public void Mp3_gain_changes_every_granule_and_updates_the_checksum()
    {
        var file = (byte[])[.. "ID3"u8, 3, 0, 0, 0, 0, 0, 2, 0xAA, 0xBB, .. Mp3Frame(200, 201, 202, 203), .. Mp3Frame(150, 151, 152, 153), .. "TAG"u8, .. new byte[125]];

        Assert.Equal(-3, Mp3Gain.Adjust(file, -3));

        int[] Gains(int frame) => [.. Enumerable.Range(0, 4).Select(i => GetBits(file, (12 + frame * 417 + 6) * 8 + 20 + i * 59 + 21, 8))];
        Assert.Equal([197, 198, 199, 200], Gains(0));
        Assert.Equal([147, 148, 149, 150], Gains(1));
        var start = 12 + 417;
        var crc = Mp3Gain.Crc16(file.AsSpan(start + 2, 2), 0xFFFF);
        crc = Mp3Gain.Crc16(file.AsSpan(start + 6, 32), crc);
        Assert.Equal(crc, file[start + 4] << 8 | file[start + 5]);
        Assert.Equal([0xAA, 0xBB], file[10..12]); // tag untouched
    }

    [Fact]
    public void Mp3_gain_is_limited_so_no_granule_wraps_around()
    {
        var file = Mp3Frame(250, 100, 100, 100);
        Assert.Equal(5, Mp3Gain.Adjust(file, 10));
        Assert.Equal(255, GetBits(file, 6 * 8 + 20 + 21, 8));
    }

    /// <summary>A Layer III frame with CRC; its granules start at <paramref name="firstBlock"/> bits into the side info.</summary>
    private static (byte[] Frame, int[] GainBits) Frame(byte b1, byte b2, byte b3, int length, int firstBlock, int blockBits, int blocks, int gain)
    {
        var frame = new byte[length];
        frame[0] = 0xFF; frame[1] = b1; frame[2] = b2; frame[3] = b3;
        var gainBits = new int[blocks];
        for (var i = 0; i < blocks; i++)
        {
            var block = 6 * 8 + firstBlock + i * blockBits;
            SetBits(frame, block, 12, 100);
            SetBits(frame, block + 21, 8, gain + i);
            gainBits[i] = block + 21;
        }
        return (frame, gainBits);
    }

    [Theory]
    [InlineData(0xFA, 0x90, 0xC0, 417, 18, 59, 2, 17)]   // MPEG-1 mono, 128 kbps, 44.1 kHz
    [InlineData(0xF2, 0x80, 0x00, 208, 10, 63, 2, 17)]   // MPEG-2 stereo, 64 kbps, 22.05 kHz
    [InlineData(0xF2, 0x80, 0xC0, 208, 9, 63, 1, 9)]     // MPEG-2 mono
    [InlineData(0xE2, 0x40, 0xC0, 208, 9, 63, 1, 9)]     // MPEG-2.5 mono, 32 kbps, 11.025 kHz
    public void Every_mpeg_version_and_channel_mode_is_adjusted(int b1, int b2, int b3, int length, int firstBlock, int blockBits, int blocks, int sideInfo)
    {
        var (frame, gainBits) = Frame((byte)b1, (byte)b2, (byte)b3, length, firstBlock, blockBits, blocks, 180);
        var file = (byte[])[.. frame, .. frame];

        Assert.Equal(-3, Mp3Gain.Adjust(file, -3));

        for (var f = 0; f < 2; f++)
        {
            var start = f * length;
            for (var i = 0; i < blocks; i++) Assert.Equal(177 + i, GetBits(file, start * 8 + gainBits[i], 8));
            var crc = Mp3Gain.Crc16(file.AsSpan(start + 2, 2), 0xFFFF);
            crc = Mp3Gain.Crc16(file.AsSpan(start + 6, sideInfo), crc);
            Assert.Equal(crc, file[start + 4] << 8 | file[start + 5]);
        }
    }

    [Fact]
    public void The_info_frame_is_left_alone_and_junk_before_the_audio_is_skipped()
    {
        var info = Mp3Frame(100, 100, 100, 100);
        "Info"u8.CopyTo(info.AsSpan(6 + 32));
        var junk = new byte[] { 0xFF, 0xFB, 0x00, 0x12, 0xFF, 0x01, 0x02 };
        var file = (byte[])[.. junk, .. info, .. Mp3Frame(200, 200, 200, 200)];
        var infoBefore = file[junk.Length..(junk.Length + 417)];

        Assert.Equal(2, Mp3Gain.Adjust(file, 2));

        Assert.Equal(infoBefore, file[junk.Length..(junk.Length + 417)]);
        Assert.Equal(202, GetBits(file, (junk.Length + 417 + 6) * 8 + 20 + 21, 8));
    }

    [Fact]
    public void A_file_without_mp3_frames_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => Mp3Gain.Adjust(Encoding.ASCII.GetBytes("not an mp3 at all"), 1));
    }

    [Fact]
    public void A_file_cant_replace_itself()
    {
        var dir = Directory.CreateTempSubdirectory("songify-swap-");
        try
        {
            var song = Path.Combine(dir.FullName, "song.mp3");
            File.WriteAllText(song, "original");
            Assert.Throws<InvalidOperationException>(() => FileSwap.ReplaceRecycling(song, song, File.Delete));
            Assert.Equal("original", File.ReadAllText(song));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void If_the_new_version_cant_take_the_originals_place_it_is_kept_beside_it()
    {
        var dir = Directory.CreateTempSubdirectory("songify-swap-");
        try
        {
            var song = Path.Combine(dir.FullName, "song.mp3");
            var levelled = Path.Combine(dir.FullName, "levelled.tmp");
            File.WriteAllText(song, "original");
            File.WriteAllText(levelled, "levelled");
            FileStream? blocker = null;

            var ex = Assert.Throws<InvalidOperationException>(() => FileSwap.ReplaceRecycling(levelled, song, path =>
            {
                File.Move(path, Path.Combine(dir.FullName, "bin.mp3"));
                blocker = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); // something takes the name
            }));
            blocker!.Dispose();

            Assert.Contains("song (levelled).mp3", ex.Message);
            Assert.Equal("levelled", File.ReadAllText(Path.Combine(dir.FullName, "song (levelled).mp3")));
            Assert.Equal("original", File.ReadAllText(Path.Combine(dir.FullName, "bin.mp3")));
            Assert.Empty(Directory.GetFiles(dir.FullName, "~songify-*"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_file_that_is_not_ogg_opus_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => OpusGain.Adjust(Encoding.ASCII.GetBytes("OggS but far too short"), 1));
    }
}

/// <summary>Levels real files with the real ffmpeg (opt-in: SONGIFY_INTEGRATION=1, tools already downloaded).</summary>
public sealed class LevellingIntegrationTests : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("songify-level-");
    private readonly ToolManager _tools;
    private readonly AudioEncoder _encoder;
    private readonly VolumeLeveller _leveller;
    private readonly string _songs, _copies, _bin;
    private static bool Enabled => Environment.GetEnvironmentVariable("SONGIFY_INTEGRATION") == "1";

    public LevellingIntegrationTests()
    {
        _tools = new ToolManager(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Songify", "tools"), _http);
        _encoder = new AudioEncoder(_tools);
        _leveller = new VolumeLeveller(_encoder, _tools, Directory.CreateDirectory(Path.Combine(_dir.FullName, "work")).FullName);
        _songs = Directory.CreateDirectory(Path.Combine(_dir.FullName, "songs")).FullName;
        _copies = Path.Combine(_dir.FullName, "copies");
        _bin = Directory.CreateDirectory(Path.Combine(_dir.FullName, "bin")).FullName;
    }

    public void Dispose()
    {
        _http.Dispose();
        _dir.Delete(recursive: true);
    }

    /// <summary>Stands in for the Recycle Bin.</summary>
    private void Recycle(string path) => File.Move(path, Path.Combine(_bin, Path.GetFileName(path)));

    /// <summary>The test songs are about -27 LUFS: this is a clear cut.</summary>
    private const double Target = -32;

    private static readonly TrackTags Tags = new("Song", "Artist", "Album", "Artist", null, 2020);
    private static readonly Dictionary<string, string> Genre = new() { ["genre"] = "Disco", ["REPLAYGAIN_TRACK_GAIN"] = "-3.00 dB" };

    /// <summary>A song as Songify saves it: tags (and a ReplayGain tag from another app), cover, noise at <paramref name="volumeDb"/>.</summary>
    private async Task<string> SongAsync(AudioFormat format, double volumeDb = -6)
    {
        var source = Path.Combine(_dir.FullName, "source.wav");
        var cover = Path.Combine(_dir.FullName, "cover.jpg");
        Assert.Equal(0, (await ProcessRunner.RunAsync(_tools.Ffmpeg,
        [
            "-nostdin", "-y", "-f", "lavfi", "-i", "anoisesrc=d=8:c=pink:a=0.5:r=48000",
            "-af", FormattableString.Invariant($"volume={volumeDb}dB"), "-ac", "2", source,
        ], default)).ExitCode);
        Assert.Equal(0, (await ProcessRunner.RunAsync(_tools.Ffmpeg,
            ["-nostdin", "-y", "-f", "lavfi", "-i", "color=c=red:s=300x300:d=1", "-frames:v", "1", cover], default)).ExitCode);
        var plan = format == AudioFormat.Original
            ? AudioEncoder.PlanOutput(AudioFormat.Opus, 160, null, null) // Songify's usual "Original": Ogg Opus
            : AudioEncoder.PlanOutput(format, 192, null, null);
        var path = Path.Combine(_songs, "Artist - Song" + plan.Extension);
        await _encoder.EncodeAsync(source, path, plan, Tags, plan.Cover == CoverEmbedding.None ? null : cover, _dir.FullName, default,
            keptTags: format == AudioFormat.Wav ? null : Genre);
        return path;
    }

    [Theory]
    [InlineData(AudioFormat.Mp3, LevelMethod.Mp3Gain)]
    [InlineData(AudioFormat.Original, LevelMethod.OpusHeader)]
    [InlineData(AudioFormat.Flac, LevelMethod.Rewrite)]
    [InlineData(AudioFormat.Wav, LevelMethod.Rewrite)]
    [InlineData(AudioFormat.Aac, LevelMethod.Reencode)]
    [InlineData(AudioFormat.Vorbis, LevelMethod.Reencode)]
    public async Task A_copy_is_levelled_to_the_target_and_keeps_tags_and_cover(AudioFormat format, LevelMethod method)
    {
        if (!Enabled) return;
        var song = await SongAsync(format);
        var original = await File.ReadAllBytesAsync(song);
        var before = await _leveller.AnalyzeAsync(song, default);
        Assert.Equal(method, before.Method);
        var gain = VolumeLeveller.PlanGain(before, Target);
        Assert.True(gain < -1, $"expected a cut, got {gain} from {before.Loudness}");

        var copy = Path.Combine(_copies, Path.GetFileName(song));
        var applied = await _leveller.LevelAsync(song, copy, before, gain, Recycle, default);

        Assert.Equal(gain, applied, 2);
        Assert.Equal(original, await File.ReadAllBytesAsync(song)); // the original is untouched
        var after = await _leveller.AnalyzeAsync(copy, default);
        // Re-encoding adds a little noise, which measures slightly louder.
        var tolerance = method switch { LevelMethod.Mp3Gain => 0.9, LevelMethod.Reencode => 0.5, _ => 0.3 };
        Assert.Equal(before.Loudness.IntegratedLufs + applied, after.Loudness.IntegratedLufs, tolerance);
        Assert.Equal(Target, after.Loudness.IntegratedLufs, tolerance);
        var tags = await _encoder.ReadTagsAsync(copy, default);
        Assert.Equal((await _encoder.ReadTagsAsync(song, default)).Tags, tags.Tags);
        if (format != AudioFormat.Wav)
        {
            Assert.True(tags.HasCover);
            Assert.Equal("Disco", tags.Other.GetValueOrDefault("genre"));
            Assert.DoesNotContain(tags.Other.Keys, VolumeLeveller.IsLevelTag); // would now be wrong
        }
        Assert.Empty(Directory.GetFiles(_copies, "~songify-*")); // no staged leftovers
        // The audio still decodes cleanly (Ogg and MP3 checksums are right).
        var check = await ProcessRunner.RunAsync(_tools.Ffmpeg, ["-v", "error", "-err_detect", "crccheck", "-i", copy, "-f", "null", "-"], default);
        Assert.Equal("", check.StdErr.Trim());
    }

    [Fact]
    public async Task Mp3_without_gain_tags_keeps_its_bytes_apart_from_the_gains()
    {
        if (!Enabled) return;
        var source = await SongAsync(AudioFormat.Wav);
        var song = Path.Combine(_songs, "plain.mp3");
        await _encoder.EncodeAsync(source, song, AudioEncoder.PlanOutput(AudioFormat.Mp3, 320, null, null), Tags, null, _dir.FullName, default);
        var before = await _leveller.AnalyzeAsync(song, default);

        var copy = Path.Combine(_copies, "plain.mp3");
        await _leveller.LevelAsync(song, copy, before, -2 * Mp3Gain.StepDb, Recycle, default);

        var a = await File.ReadAllBytesAsync(song);
        var b = await File.ReadAllBytesAsync(copy);
        Assert.Equal(a.Length, b.Length);
        var changed = a.Zip(b).Count(p => p.First != p.Second);
        Assert.InRange(changed, 1, a.Length / 50); // only side info bytes and checksums
    }

    [Fact]
    public async Task Opus_without_gain_tags_changes_only_its_header_page()
    {
        if (!Enabled) return;
        var source = await SongAsync(AudioFormat.Wav);
        var song = Path.Combine(_songs, "plain.opus");
        await _encoder.EncodeAsync(source, song, AudioEncoder.PlanOutput(AudioFormat.Opus, 160, null, null), Tags, null, _dir.FullName, default);
        var original = await File.ReadAllBytesAsync(song);
        var unchanged = (byte[])original.Clone();
        Assert.Equal(0, OpusGain.Adjust(unchanged, 0));
        Assert.Equal(original, unchanged);
        var before = await _leveller.AnalyzeAsync(song, default);

        var copy = Path.Combine(_copies, "plain.opus");
        var applied = await _leveller.LevelAsync(song, copy, before, VolumeLeveller.PlanGain(before, Target), Recycle, default);

        var levelled = await File.ReadAllBytesAsync(copy);
        Assert.Equal(original.Length, levelled.Length);
        Assert.Equal(original[200..], levelled[200..]); // only the first page (OpusHead) differs
        Assert.Equal(before.Loudness.IntegratedLufs + applied, (await _leveller.AnalyzeAsync(copy, default)).Loudness.IntegratedLufs, 0.2);
        var check = await ProcessRunner.RunAsync(_tools.Ffmpeg, ["-v", "error", "-err_detect", "crccheck", "-i", copy, "-f", "null", "-"], default);
        Assert.Equal("", check.StdErr.Trim());
    }

    [Fact]
    public async Task A_low_rate_mono_mp3_is_levelled()
    {
        if (!Enabled) return;
        var source = await SongAsync(AudioFormat.Wav);
        var song = Path.Combine(_songs, "mono.mp3");
        Assert.Equal(0, (await ProcessRunner.RunAsync(_tools.Ffmpeg,
            ["-nostdin", "-y", "-i", source, "-ar", "22050", "-ac", "1", "-c:a", "libmp3lame", "-b:a", "64k", "-write_xing", "1", song], default)).ExitCode);
        var before = await _leveller.AnalyzeAsync(song, default);

        var copy = Path.Combine(_copies, "mono.mp3");
        var applied = await _leveller.LevelAsync(song, copy, before, VolumeLeveller.PlanGain(before, Target), Recycle, default);

        Assert.True(applied < -1);
        Assert.Equal(before.Loudness.IntegratedLufs + applied, (await _leveller.AnalyzeAsync(copy, default)).Loudness.IntegratedLufs, 0.3);
    }

    [Fact]
    public async Task A_quiet_song_is_turned_up_without_clipping()
    {
        if (!Enabled) return;
        var song = await SongAsync(AudioFormat.Flac, volumeDb: -20);
        var before = await _leveller.AnalyzeAsync(song, default);
        var gain = VolumeLeveller.PlanGain(before, -5);
        Assert.True(gain > 0);

        var copy = Path.Combine(_copies, Path.GetFileName(song));
        await _leveller.LevelAsync(song, copy, before, gain, Recycle, default);

        var after = await _leveller.AnalyzeAsync(copy, default);
        Assert.InRange(after.Loudness.TruePeakDb, VolumeLeveller.PeakCeilingDb - 0.5, VolumeLeveller.PeakCeilingDb + 0.2);
    }

    [Fact]
    public async Task Levelling_in_place_sends_the_original_to_the_recycle_bin()
    {
        if (!Enabled) return;
        var song = await SongAsync(AudioFormat.Mp3);
        var original = await File.ReadAllBytesAsync(song);
        var before = await _leveller.AnalyzeAsync(song, default);

        await _leveller.LevelAsync(song, null, before, VolumeLeveller.PlanGain(before, Target), Recycle, default);

        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(_bin, Path.GetFileName(song))));
        Assert.Equal(Target, (await _leveller.AnalyzeAsync(song, default)).Loudness.IntegratedLufs, 0.9);
        Assert.Single(Directory.GetFiles(_songs));
    }

    [Fact]
    public async Task A_copy_replaces_its_original_which_goes_to_the_recycle_bin()
    {
        if (!Enabled) return;
        var song = await SongAsync(AudioFormat.Original);
        var original = await File.ReadAllBytesAsync(song);
        var before = await _leveller.AnalyzeAsync(song, default);
        var copy = Path.Combine(_copies, Path.GetFileName(song));
        await _leveller.LevelAsync(song, copy, before, VolumeLeveller.PlanGain(before, Target), Recycle, default);
        var levelled = await File.ReadAllBytesAsync(copy);

        await VolumeLeveller.ReplaceWithCopyAsync(song, copy, Recycle);

        Assert.Equal(levelled, await File.ReadAllBytesAsync(song));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(_bin, Path.GetFileName(song))));
        Assert.False(File.Exists(copy));
    }

    [Fact]
    public async Task If_the_original_cant_be_recycled_nothing_changes()
    {
        if (!Enabled) return;
        var song = await SongAsync(AudioFormat.Original);
        var original = await File.ReadAllBytesAsync(song);
        var before = await _leveller.AnalyzeAsync(song, default);
        var copy = Path.Combine(_copies, Path.GetFileName(song));
        await _leveller.LevelAsync(song, copy, before, VolumeLeveller.PlanGain(before, Target), Recycle, default);

        await Assert.ThrowsAsync<IOException>(() =>
            VolumeLeveller.ReplaceWithCopyAsync(song, copy, _ => throw new IOException("in use")));

        Assert.Equal(original, await File.ReadAllBytesAsync(song));
        Assert.True(File.Exists(copy));
        Assert.Single(Directory.GetFiles(_songs)); // no staged leftovers
    }
}
