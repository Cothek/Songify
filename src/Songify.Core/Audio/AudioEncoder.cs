using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Tools;

namespace Songify.Core.Audio;

/// <summary>Tags written to the file. <paramref name="Year"/> is the release year, written as the "date" tag.</summary>
public sealed record TrackTags(string Title, string? Artist, string? Album, string? AlbumArtist, string? Comment, int? Year = null);

/// <summary>What a saved file holds: its tags, and whether it has cover art.</summary>
/// <param name="Other">Tags Songify doesn't edit (genre, track number…), kept when the file is re-tagged.</param>
public sealed record FileTags(TrackTags Tags, bool HasCover, IReadOnlyDictionary<string, string> Other);

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
        var c = codec?.ToLowerInvariant() ?? "";
        var (extension, name) =
            c == "opus" ? (".opus", "Opus")
            : c.StartsWith("mp4a", StringComparison.Ordinal) || c == "aac" ? (".m4a", "AAC")
            : c == "vorbis" ? (".ogg", "Vorbis")
            : c == "mp3" ? (".mp3", "MP3")
            : (".mka", string.IsNullOrEmpty(codec) ? "Unknown codec" : codec!);
        var quality = (kbps is > 0 ? KbpsLabel(name, kbps.Value) : name) + " (original)";
        return PlanRetag(extension) with { Quality = quality };
    }

    /// <summary>Copies the audio into a container as-is (no re-encoding), with that container's way of storing a cover.</summary>
    public static OutputPlan PlanRetag(string extension)
    {
        var ext = extension.ToLowerInvariant();
        var cover = ext switch
        {
            ".opus" or ".ogg" => CoverEmbedding.VorbisComment,
            ".mp3" or ".m4a" or ".flac" => CoverEmbedding.AttachedPicture,
            _ => CoverEmbedding.None,
        };
        string[] args = ext == ".mp3" ? ["-c:a", "copy", .. Id3v23] : ["-c:a", "copy"];
        return new OutputPlan(ext, args, cover, "");
    }

    /// <summary>Reads a saved file's tags with ffprobe.</summary>
    public async Task<FileTags> ReadTagsAsync(string path, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(tools.Ffprobe,
            ["-v", "error", "-show_entries", "format_tags:stream_tags:stream=codec_type", "-of", "json", path], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not read the file's tags: {result.ErrorSummary}");
        return ParseProbeTags(result.StdOut, Path.GetFileNameWithoutExtension(path));
    }

    private static readonly string[] TitleKeys = ["title"], ArtistKeys = ["artist"], AlbumKeys = ["album"],
        AlbumArtistKeys = ["album_artist", "albumartist", "album artist"], CommentKeys = ["comment", "description"],
        DateKeys = ["date", "year"];

    /// <summary>Tags that describe the container or encoder, not the song: ffmpeg writes its own.</summary>
    private static readonly string[] TechnicalKeys =
        ["encoder", "major_brand", "minor_version", "compatible_brands", "handler_name", "vendor_id", "language",
         "creation_time", "duration", "metadata_block_picture"];

    /// <summary>
    /// ffprobe JSON → tags. Containers differ in where tags live (Ogg: on the audio stream; MP3, M4A: on the
    /// file) and in key case, so both places are read, case-insensitively. A cover shows up as a video stream.
    /// Tags Songify doesn't edit (genre, track number… added in another app) are returned to be kept.
    /// </summary>
    public static FileTags ParseProbeTags(string json, string fallbackTitle)
    {
        var root = JsonNode.Parse(json);
        var streams = root?["streams"] as JsonArray ?? [];
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(JsonNode? tags)
        {
            if (tags is not JsonObject o) return;
            foreach (var (key, _) in o)
                if (JsonValues.Text(o, key) is { } value) values.TryAdd(key, value.Trim());
        }
        Add(root?["format"]?["tags"]);
        foreach (var stream in streams.Where(s => JsonValues.Text(s, "codec_type") == "audio"))
            Add(stream?["tags"]);

        string? Get(string[] keys) => keys.Select(values.GetValueOrDefault).FirstOrDefault(v => v is not null);
        var tags = new TrackTags(
            Get(TitleKeys) ?? fallbackTitle, Get(ArtistKeys), Get(AlbumKeys), Get(AlbumArtistKeys), Get(CommentKeys),
            ReleaseYear.FromDate(Get(DateKeys)));

        var known = new HashSet<string>(
            [.. TitleKeys, .. ArtistKeys, .. AlbumKeys, .. AlbumArtistKeys, .. CommentKeys, .. DateKeys, .. TechnicalKeys],
            StringComparer.OrdinalIgnoreCase);
        var other = values.Where(v => !known.Contains(v.Key) && !v.Key.Contains('='))
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        return new FileTags(tags, streams.Any(s => JsonValues.Text(s, "codec_type") == "video"), other);
    }

    /// <summary>Copies a file's embedded cover out unchanged. Null if it has none or it can't be read.</summary>
    public async Task<string?> TryExtractCoverAsync(string path, string workDir, CancellationToken ct)
    {
        var cover = Path.Combine(workDir, "cover.jpg");
        var result = await ProcessRunner.RunAsync(tools.Ffmpeg,
            ["-hide_banner", "-nostdin", "-y", "-i", path, "-map", "0:v:0", "-c:v", "copy", "-f", "image2", "-update", "1", cover], ct);
        if (result.ExitCode == 0 && File.Exists(cover)) return cover;
        AppLog.Warn($"Could not read the cover of {Path.GetFileName(path)}: {result.ErrorSummary}");
        return null;
    }

    /// <param name="keptTags">
    /// Set when re-tagging a saved file: its other tags, written back unchanged. The cover then also came from the
    /// file and is embedded as-is instead of being cropped and re-compressed.
    /// </param>
    public async Task EncodeAsync(
        string input, string output, OutputPlan plan, TrackTags tags, string? coverPath, string workDir, CancellationToken ct,
        IReadOnlyDictionary<string, string>? keptTags = null)
    {
        string? metadataFile = null;
        if (plan.Cover == CoverEmbedding.VorbisComment)
        {
            // Ogg tags go through an ffmetadata file: an embedded picture is far longer than a command line allows.
            var picture = coverPath is null ? null
                : keptTags is not null && await FinalCoverBlockAsync(coverPath, ct) is { } final ? final
                : await TrySquareCoverAsync(coverPath, workDir, ct);
            metadataFile = Path.Combine(workDir, "tags.ffmeta");
            await File.WriteAllTextAsync(metadataFile, BuildFfMetadata(tags, picture, keptTags), ct);
        }

        var attachedCover = plan.Cover == CoverEmbedding.AttachedPicture ? coverPath : null;
        var result = await ProcessRunner.RunAsync(tools.Ffmpeg, BuildArgs(input, output, plan, tags, attachedCover, metadataFile, keptTags), ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {result.ErrorSummary}");
    }

    public static List<string> BuildArgs(
        string input, string output, OutputPlan plan, TrackTags tags, string? attachedCover, string? metadataFile,
        IReadOnlyDictionary<string, string>? keptTags = null)
    {
        var coverIsFinal = keptTags is not null;
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
            args.AddRange(["-map", $"{coverInput}:v:0"]);
            args.AddRange(coverIsFinal ? ["-c:v", "copy"] : ["-c:v", "mjpeg", "-vf", CropToSquare]);
            args.AddRange(
            [
                "-disposition:v:0", "attached_pic",
                "-metadata:s:v", "title=Album cover",
                "-metadata:s:v", "comment=Cover (front)",
            ]);
        }

        args.AddRange(plan.AudioArgs);

        if (metadataFile is null)
            foreach (var (key, value) in TagPairs(tags, keptTags))
                args.AddRange(["-metadata", $"{key}={value}"]);

        args.Add(output);
        return args;
    }

    // Video thumbnails are 16:9; covers are square.
    private const string CropToSquare = @"crop=min(iw\,ih):min(iw\,ih)";

    private static IEnumerable<(string Key, string Value)> TagPairs(TrackTags tags, IReadOnlyDictionary<string, string>? kept) =>
        new (string Key, string? Value)[]
        {
            ("title", tags.Title), ("artist", tags.Artist), ("album", tags.Album),
            ("album_artist", tags.AlbumArtist), ("date", tags.Year?.ToString(CultureInfo.InvariantCulture)),
            ("comment", tags.Comment),
        }
        .Concat((kept ?? new Dictionary<string, string>()).Select(t => (Key: t.Key, Value: (string?)t.Value)))
        .Where(t => !string.IsNullOrWhiteSpace(t.Value))
        .Select(t => (t.Key, t.Value!));

    /// <summary>ffmpeg's metadata file format; <paramref name="picture"/> is a FLAC picture block.</summary>
    public static string BuildFfMetadata(TrackTags tags, byte[]? picture, IReadOnlyDictionary<string, string>? keptTags = null)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        foreach (var (key, value) in TagPairs(tags, keptTags))
            sb.Append(EscapeFfMetadata(key)).Append('=').Append(EscapeFfMetadata(value)).Append('\n');
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

    /// <summary>An Ogg picture block for a cover taken from a saved file. Null if it isn't a JPEG.</summary>
    private static async Task<byte[]?> FinalCoverBlockAsync(string coverPath, CancellationToken ct)
    {
        var jpeg = await File.ReadAllBytesAsync(coverPath, ct);
        return JpegSize(jpeg) is var (width, height) ? FlacPictureBlock(jpeg, width, height) : null;
    }

    /// <summary>Width and height from a JPEG's start-of-frame segment, or null if it isn't a JPEG.</summary>
    public static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return null;
        var i = 2;
        while (i + 9 < jpeg.Length)
        {
            if (jpeg[i] != 0xFF) return null;
            var marker = jpeg[i + 1];
            if (marker == 0xFF) { i++; continue; } // fill byte
            // SOF0–SOF15, except DHT (C4), JPG (C8) and DAC (CC)
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (BinaryPrimitives.ReadUInt16BigEndian(jpeg[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(jpeg[(i + 5)..]));
            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg[(i + 2)..]);
        }
        return null;
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
