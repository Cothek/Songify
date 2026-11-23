using System.Text.Json.Nodes;
using Songify.Core.Infrastructure;
using Songify.Core.Tools;

namespace Songify.Core.YouTube;

public sealed record VideoEntry(string Id, string Title, string? Channel, int? DurationSeconds);

public sealed record PlaylistInfo(string Title, IReadOnlyList<VideoEntry> Entries);

/// <summary>
/// A downloaded audio file with yt-dlp's codec name (e.g. "opus", "mp4a.40.2") and average bitrate.
/// Album and release year come from the official "Provided to YouTube" description, so they are
/// only present for album tracks.
/// </summary>
public sealed record DownloadedAudio(string Path, string? Codec, double? BitrateKbps, string? Album = null, int? ReleaseYear = null);

public sealed class YtDlpException(string message) : Exception(message);

/// <summary>Thin wrapper over the yt-dlp executable.</summary>
public sealed class YtDlpClient(ToolManager tools)
{
    private static readonly Dictionary<string, string> Utf8Env = new()
    {
        ["PYTHONUTF8"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
    };

    private static readonly HashSet<string> UnavailableTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "[Private video]", "[Deleted video]", "[Unavailable video]",
    };

    private IEnumerable<string> CommonArgs() =>
    [
        "--ignore-config",
        "--no-progress",
        "--js-runtimes", $"deno:{tools.Deno}",
        "--ffmpeg-location", tools.ToolsDir,
    ];

    /// <summary>Lists a playlist (or a single video URL) without resolving each video.</summary>
    public async Task<PlaylistInfo> GetPlaylistAsync(string url, CancellationToken ct)
    {
        var result = await RunAsync([.. CommonArgs(), "--flat-playlist", "--yes-playlist", "-J", url], ct);
        var root = JsonNode.Parse(result.StdOut) ?? throw new YtDlpException("yt-dlp returned no data");
        return ParsePlaylist(root);
    }

    public static PlaylistInfo ParsePlaylist(JsonNode root)
    {
        var title = (string?)root["title"] ?? "Untitled";
        if ((string?)root["_type"] != "playlist")
        {
            var single = ToEntry(root);
            return new PlaylistInfo(title, single is null ? [] : [single]);
        }

        var entries = root["entries"]?.AsArray()
            .Select(e => e is null ? null : ToEntry(e))
            .OfType<VideoEntry>()
            .ToList() ?? [];
        return new PlaylistInfo(title, entries);
    }

    private static VideoEntry? ToEntry(JsonNode node)
    {
        var id = (string?)node["id"];
        var title = (string?)node["title"];
        // Channel/tab URLs return nested playlists; only real video ids are songs.
        if (!YouTubeUrls.IsVideoId(id) || string.IsNullOrEmpty(title) || UnavailableTitles.Contains(title))
            return null;
        var channel = (string?)node["channel"] ?? (string?)node["uploader"];
        int? duration = Number(node, "duration") is double secs ? (int)Math.Round(secs) : null;
        return new VideoEntry(id!, title, channel, duration);
    }

    /// <summary>
    /// Downloads the best audio stream as-is (no re-encode). Prefers Opus, YouTube's best audio,
    /// so "Original" saves Opus; converted formats start from it too.
    /// </summary>
    public async Task<DownloadedAudio> DownloadAudioAsync(string videoId, bool fromYouTubeMusic, string workDir, CancellationToken ct)
    {
        var url = fromYouTubeMusic ? YouTubeUrls.Music(videoId) : YouTubeUrls.Video(videoId);
        var result = await RunAsync(
        [
            .. CommonArgs(),
            "-f", "bestaudio[acodec=opus]/bestaudio/best",
            "--no-playlist",
            "-o", Path.Combine(workDir, "source.%(ext)s"),
            "--print", "after_move:%(.{filepath,acodec,abr,album,release_year})j",
            url,
        ], ct);

        var downloaded = ParseDownload(ProcessResult.Lines(result.StdOut).LastOrDefault());
        if (downloaded is null || !File.Exists(downloaded.Path))
            throw new YtDlpException($"yt-dlp finished but no file was produced for {videoId}");
        return downloaded;
    }

    public static DownloadedAudio? ParseDownload(string? printedJson)
    {
        if (string.IsNullOrWhiteSpace(printedJson)) return null;
        try
        {
            if (JsonNode.Parse(printedJson) is not JsonObject node) return null;
            var path = Text(node, "filepath");
            if (path is null) return null;
            int? year = Number(node, "release_year") is double y && y is > 1000 and < 3000 ? (int)y : null;
            return new DownloadedAudio(path, Text(node, "acodec"), Number(node, "abr"), Text(node, "album"), year);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The string value, or null when missing, not a string, or blank.</summary>
    private static string? Text(JsonNode node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static double? Number(JsonNode node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private async Task<ProcessResult> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(tools.YtDlp, args, ct, Utf8Env);
        if (result.ExitCode != 0)
            throw new YtDlpException(result.ErrorSummary);
        return result;
    }
}
