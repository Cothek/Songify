using System.Buffers.Binary;

namespace Songify.Core.Audio;

/// <summary>
/// Changes an Ogg Opus file's volume without re-encoding: the OpusHead header has an output gain (dB, Q7.8) that
/// every decoder must apply (RFC 7845, section 5.1). Only that field and its page's checksum change.
/// </summary>
public static class OpusGain
{
    private const int PageHeaderLength = 27;
    private const int GainOffset = 16; // in the OpusHead packet

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>
    /// Adds <paramref name="gainDb"/> to the output gain in <paramref name="file"/> (in place), limited to what the
    /// field holds. Returns the change applied, in dB.
    /// </summary>
    /// <exception cref="InvalidDataException">Not an Ogg Opus file.</exception>
    public static double Adjust(byte[] file, double gainDb)
    {
        if (file.Length < PageHeaderLength || !file.AsSpan(0, 4).SequenceEqual("OggS"u8))
            throw new InvalidDataException("Not an Ogg file.");
        var segments = file[26];
        var packet = PageHeaderLength + segments;
        var pageLength = packet;
        for (var i = 0; i < segments; i++) pageLength += file[PageHeaderLength + i];
        if (pageLength > file.Length || pageLength - packet < GainOffset + 2
            || !file.AsSpan(packet, 8).SequenceEqual("OpusHead"u8))
            throw new InvalidDataException("Not an Ogg Opus file.");

        var gainField = file.AsSpan(packet + GainOffset, 2);
        var old = BinaryPrimitives.ReadInt16LittleEndian(gainField);
        var updated = (short)Math.Clamp(old + (int)Math.Round(gainDb * 256), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(gainField, updated);
        WriteChecksum(file.AsSpan(0, pageLength));
        return (updated - old) / 256.0;
    }

    /// <summary>The page checksum: CRC-32 (polynomial 0x04C11DB7, start 0, not reflected) with its own field zeroed.</summary>
    private static void WriteChecksum(Span<byte> page)
    {
        page.Slice(22, 4).Clear();
        uint crc = 0;
        foreach (var b in page) crc = crc << 8 ^ CrcTable[(crc >> 24 ^ b) & 0xFF];
        BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(22, 4), crc);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var bit = 0; bit < 8; bit++) r = (r & 0x8000_0000) != 0 ? r << 1 ^ 0x04C1_1DB7 : r << 1;
            table[i] = r;
        }
        return table;
    }
}
