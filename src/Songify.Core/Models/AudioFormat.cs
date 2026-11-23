namespace Songify.Core.Models;

public enum AudioFormat
{
    Mp3,
    Aac,
    Opus,
    Vorbis,
    Flac,
    Wav,
    /// <summary>Keep the downloaded stream as-is (YouTube: usually Opus): no conversion, no quality loss.</summary>
    Original,
}

public static class AudioFormatInfo
{
    public static readonly IReadOnlyList<int> Bitrates = [96, 128, 160, 192, 256, 320];

    public static string DisplayName(this AudioFormat format) => format switch
    {
        AudioFormat.Original => "Original (no conversion)",
        AudioFormat.Mp3 => "MP3",
        AudioFormat.Aac => "AAC (.m4a)",
        AudioFormat.Opus => "Opus",
        AudioFormat.Vorbis => "Ogg Vorbis",
        AudioFormat.Flac => "FLAC (lossless)",
        AudioFormat.Wav => "WAV (uncompressed)",
        _ => format.ToString(),
    };

    public static bool IsLossless(this AudioFormat format) => format is AudioFormat.Flac or AudioFormat.Wav;

    /// <summary>False for lossless formats and for Original, which keeps the source's bitrate.</summary>
    public static bool UsesBitrate(this AudioFormat format) => !format.IsLossless() && format != AudioFormat.Original;
}
