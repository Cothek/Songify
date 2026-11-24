using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Tools;

namespace Songify.Core.Audio;

/// <summary>Tags written to the file. <paramref name="Year"/> is the release year, written as the "date" tag.</summary>
public sealed record TrackTags(string Title, string? Artist, string? Album, string? AlbumArtist, string? Comment, int? Year = null);

/// <summary>How a container stores cover art.</summary>
public enum CoverEmbedding
{
    None,
    /// <summary>MP3, M4A, FLAC: an attached-picture video stream.</summary>
    AttachedPicture,
    /// <summary>Ogg (Opus, Vorbis): a base64 METADATA_BLOCK_PICTURE comment, which ffmpeg can't build itself.</summary>
    VorbisComment,
}

/// <summary>What ffmpeg should produce: container extension, audio codec arguments, and a label for the UI.</summary>
public sealed record OutputPlan(string Extension, IReadOnlyList<string> AudioArgs, CoverEmbedding Cover, string Quality);

/// <summary>Transcodes (or, for <see cref="AudioFormat.Original"/>, copies) downloaded audio, writing tags and cover art.</summary>
public sealed class AudioEncoder(ToolManager tools)
{
    /// <summary>Ogg covers are scaled to a known size because the picture block records its dimensions.</summary>
    private const int OggCoverSize = 544;

    /// <summary>ID3v2.3: v2.4 (ffmpeg's default) isn't read by Windows Explorer and many players.</summary>
    private static readonly string[] Id3v23 = ["-id3v2_version", "3"];

    private static string KbpsLabel(string codec, double kbps) => $"{codec} {Math.Round(kbps)} kbps";

    public static OutputPlan PlanOutput(AudioFormat format, int bitrateKbps, string? sourceCodec, double? sourceKbps)
    {
        string[] Lossy(string encoder) => ["-c:a", encoder, "-b:a", $"{bitrateKbps}k"];
        string Kbps(string codec) => KbpsLabel(codec, bitrateKbps);

        return format switch
        {
            AudioFormat.Mp3 => new(".mp3", [.. Lossy("libmp3lame"), .. Id3v23], CoverEmbedding.AttachedPicture, Kbps("MP3")),
            AudioFormat.Aac => new(".m4a", Lossy("aac"), CoverEmbedding.AttachedPicture, Kbps("AAC")),
            AudioFormat.Opus => new(".opus", Lossy("libopus"), CoverEmbedding.VorbisComment, Kbps("Opus")),
            AudioFormat.Vorbis => new(".ogg", Lossy("libvorbis"), CoverEmbedding.VorbisComment, Kbps("Vorbis")),
            AudioFormat.Flac => new(".flac", ["-c:a", "flac"], CoverEmbedding.AttachedPicture, "FLAC"),
            AudioFormat.Wav => new(".wav", ["-c:a", "pcm_s16le"], CoverEmbedding.None, "WAV"),
            AudioFormat.Original => PlanCopy(sourceCodec, sourceKbps),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    /// <summary>Spotify Free streams Ogg Vorbis at 160 kbps; "Original" saves recordings at that quality.</summary>
    public const int SpotifyFreeKbps = 160;

    /// <summary>
    /// For a Spotify recording (uncompressed): there is no stream to copy, so <see cref="AudioFormat.Original"/>
    /// becomes Vorbis at Spotify Free's own bitrate.
    /// </summary>
    public static OutputPlan PlanRecordingOutput(AudioFormat format, int bitrateKbps) =>
        format == AudioFormat.Original
            ? PlanOutput(AudioFormat.Vorbis, SpotifyFreeKbps, null, null) with { Quality = $"Vorbis {SpotifyFreeKbps} kbps (Spotify's quality)" }
            : PlanOutput(format, bitrateKbps, null, null);

    /// <summary>Keeps the source stream; picks the container that natively holds its codec.</summary>
    private static OutputPlan PlanCopy(string? codec, double? kbps)
    {
        string[] copy = ["-c:a", "copy"];
        string[] copyMp3 = [.. copy, .. Id3v23];
        var c = codec?.ToLowerInvariant() ?? "";
        var (extension, args, cover, name) =
            c == "opus" ? (".opus", copy, CoverEmbedding.VorbisComment, "Opus")
            : c.StartsWith("mp4a", StringComparison.Ordinal) || c == "aac" ? (".m4a", copy, CoverEmbedding.AttachedPicture, "AAC")
            : c == "vorbis" ? (".ogg", copy, CoverEmbedding.VorbisComment, "Vorbis")
            : c == "mp3" ? (".mp3", copyMp3, CoverEmbedding.AttachedPicture, "MP3")
            : (".mka", copy, CoverEmbedding.None, string.IsNullOrEmpty(codec) ? "Unknown codec" : codec!);
        var quality = (kbps is > 0 ? KbpsLabel(name, kbps.Value) : name) + " (original)";
        return new OutputPlan(extension, args, cover, quality);
    }

    public async Task EncodeAsync(
        string input, string output, OutputPlan plan, TrackTags tags, string? coverPath, string workDir, CancellationToken ct)
    {
        string? metadataFile = null;
        if (plan.Cover == CoverEmbedding.VorbisComment)
        {
            // Ogg tags go through an ffmetadata file: an embedded picture is far longer than a command line allows.
            var picture = coverPath is null ? null : await TrySquareCoverAsync(coverPath, workDir, ct);
            metadataFile = Path.Combine(workDir, "tags.ffmeta");
            await File.WriteAllTextAsync(metadataFile, BuildFfMetadata(tags, picture), ct);
        }

        var attachedCover = plan.Cover == CoverEmbedding.AttachedPicture ? coverPath : null;
        var result = await ProcessRunner.RunAsync(tools.Ffmpeg, BuildArgs(input, output, plan, tags, attachedCover, metadataFile), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {result.ErrorSummary}");
    }

    public static List<string> BuildArgs(
        string input, string output, OutputPlan plan, TrackTags tags, string? attachedCover, string? metadataFile)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        var nextInput = 1;
        var coverInput = attachedCover is null ? -1 : nextInput++;
        if (attachedCover is not null) args.AddRange(["-i", attachedCover]);
        var metadataInput = metadataFile is null ? -1 : nextInput++;
        if (metadataFile is not null) args.AddRange(["-f", "ffmetadata", "-i", metadataFile]);

        args.AddRange(["-map", "0:a:0"]);
        if (metadataFile is not null)
            args.AddRange(["-map_metadata", $"{metadataInput}", "-map_metadata:s:a", "-1"]);
        else
            args.AddRange(["-map_metadata", "-1"]);

        if (attachedCover is not null)
        {
            args.AddRange(
            [
                "-map", $"{coverInput}:v:0",
                "-c:v", "mjpeg",
                "-vf", CropToSquare,
                "-disposition:v:0", "attached_pic",
                "-metadata:s:v", "title=Album cover",
                "-metadata:s:v", "comment=Cover (front)",
            ]);
        }

        args.AddRange(plan.AudioArgs);

        if (metadataFile is null)
            foreach (var (key, value) in TagPairs(tags))
                args.AddRange(["-metadata", $"{key}={value}"]);

        args.Add(output);
        return args;
    }

    // Video thumbnails are 16:9; covers are square.
    private const string CropToSquare = @"crop=min(iw\,ih):min(iw\,ih)";

    private static IEnumerable<(string Key, string Value)> TagPairs(TrackTags tags) =>
        new (string Key, string? Value)[]
        {
            ("title", tags.Title), ("artist", tags.Artist), ("album", tags.Album),
            ("album_artist", tags.AlbumArtist), ("date", tags.Year?.ToString(CultureInfo.InvariantCulture)),
            ("comment", tags.Comment),
        }
        .Where(t => !string.IsNullOrWhiteSpace(t.Value))
        .Select(t => (t.Key, t.Value!));

    /// <summary>ffmpeg's metadata file format; <paramref name="picture"/> is a FLAC picture block.</summary>
    public static string BuildFfMetadata(TrackTags tags, byte[]? picture)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        foreach (var (key, value) in TagPairs(tags))
            sb.Append(key).Append('=').Append(EscapeFfMetadata(value)).Append('\n');
        if (picture is not null)
            sb.Append("METADATA_BLOCK_PICTURE=").Append(EscapeFfMetadata(Convert.ToBase64String(picture))).Append('\n');
        return sb.ToString();
    }

    private static string EscapeFfMetadata(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '=' or ';' or '#' or '\\' or '\n') sb.Append('\\');
            if (ch != '\r') sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>The FLAC METADATA_BLOCK_PICTURE layout used by Ogg comments: big-endian lengths, front cover.</summary>
    public static byte[] FlacPictureBlock(byte[] jpeg, int width, int height)
    {
        var mime = "image/jpeg"u8.ToArray();
        using var ms = new MemoryStream();
        var word = new byte[4];
        void U32(int v) { BinaryPrimitives.WriteUInt32BigEndian(word, (uint)v); ms.Write(word); }

        U32(3); // picture type: front cover
        U32(mime.Length); ms.Write(mime);
        U32(0); // description length
        U32(width); U32(height);
        U32(24); // color depth
        U32(0);  // palette size (not indexed)
        U32(jpeg.Length); ms.Write(jpeg);
        return ms.ToArray();
    }

    /// <summary>Crops and scales the cover to a square JPEG. Cover art is optional: failure means no cover.</summary>
    private async Task<byte[]?> TrySquareCoverAsync(string coverPath, string workDir, CancellationToken ct)
    {
        var square = Path.Combine(workDir, "cover-square.jpg");
        var result = await ProcessRunner.RunAsync(tools.Ffmpeg,
        [
            "-hide_banner", "-nostdin", "-y", "-i", coverPath,
            "-vf", $"{CropToSquare},scale={OggCoverSize}:{OggCoverSize}",
            "-frames:v", "1", "-update", "1", "-q:v", "2", square,
        ], ct);
        if (result.ExitCode != 0 || !File.Exists(square))
        {
            AppLog.Warn($"Could not prepare cover art: {result.ErrorSummary}");
            return null;
        }
        return FlacPictureBlock(await File.ReadAllBytesAsync(square, ct), OggCoverSize, OggCoverSize);
    }
}
