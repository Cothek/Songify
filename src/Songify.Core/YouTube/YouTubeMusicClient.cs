using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Songify.Core.YouTube;

/// <param name="Artists">Display string, e.g. "Daft Punk, Pharrell Williams &amp; Nile Rodgers".</param>
public sealed record MusicTrack(
    string VideoId,
    string Title,
    string Artists,
    string? PrimaryArtist,
    string? Album,
    int? DurationSeconds,
    string? ThumbnailUrl);

/// <summary>
/// Searches YouTube Music's "Songs" tab through its public web API (the same endpoint
/// music.youtube.com uses; no login). Songs are official album audio ("Topic" tracks).
/// </summary>
public sealed partial class YouTubeMusicClient(HttpClient http)
{
    private const string SearchUrl = "https://music.youtube.com/youtubei/v1/search?prettyPrint=false";
    private const string ClientVersion = "1.20251110.01.00";
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Search filter for the Songs tab.</summary>
    private const string SongsFilterParams = "EgWKAQIIAWoMEA4QChADEAQQCRAF";

    public async Task<IReadOnlyList<MusicTrack>> SearchSongsAsync(string query, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["context"] = new JsonObject
            {
                ["client"] = new JsonObject
                {
                    ["clientName"] = "WEB_REMIX",
                    ["clientVersion"] = ClientVersion,
                    ["hl"] = "en",
                    ["gl"] = "US",
                },
            },
            ["query"] = query,
            ["params"] = SongsFilterParams,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, SearchUrl) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "https://music.youtube.com");
        request.Headers.Add("Referer", "https://music.youtube.com/");
        request.Headers.Add("X-YouTube-Client-Name", "67");
        request.Headers.Add("X-YouTube-Client-Version", ClientVersion);
        // Skips the EU cookie-consent interstitial.
        request.Headers.Add("Cookie", "SOCS=CAI");

        // Don't let a stalled request hold a worker for the shared client's long download timeout.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SearchTimeout);
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var root = await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token);
        return root is null ? [] : ParseSearchResponse(root);
    }

    public static IReadOnlyList<MusicTrack> ParseSearchResponse(JsonNode root)
    {
        var results = new List<MusicTrack>();
        foreach (var item in FindAll(root, "musicResponsiveListItemRenderer"))
        {
            MusicTrack? track;
            try
            {
                track = ParseItem(item);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
            {
                // YouTube changed the shape of one result; skip it rather than fail the search.
                continue;
            }
            if (track is not null && results.All(r => r.VideoId != track.VideoId))
                results.Add(track);
        }
        return results;
    }

    private static MusicTrack? ParseItem(JsonNode item)
    {
        var columns = item["flexColumns"]?.AsArray();
        if (columns is null || columns.Count < 2) return null;

        var titleRuns = Runs(columns[0]);
        var detailRuns = Runs(columns[1]);
        if (titleRuns.Count == 0 || detailRuns.Count == 0) return null;

        var watch = titleRuns[0]?["navigationEndpoint"]?["watchEndpoint"];
        var videoId = (string?)item["playlistItemData"]?["videoId"] ?? (string?)watch?["videoId"];
        if (string.IsNullOrEmpty(videoId)) return null;

        // Only official audio tracks; music videos and user uploads defeat the purpose.
        var videoType = (string?)watch?["watchEndpointMusicSupportedConfigs"]?["watchEndpointMusicConfig"]?["musicVideoType"];
        if (videoType is not null && videoType != "MUSIC_VIDEO_TYPE_ATV") return null;

        var title = string.Concat(titleRuns.Select(r => (string?)r?["text"]));

        // Details look like: "Artist, Artist & Artist • Album • 4:09"
        var details = string.Concat(detailRuns.Select(r => (string?)r?["text"]));
        var segments = details.Split(" • ", StringSplitOptions.TrimEntries).ToList();
        if (segments.Count > 0 && segments[0] is "Song" or "Single" or "EP") segments.RemoveAt(0);

        int? duration = null;
        if (segments.Count > 0 && TryParseDuration(segments[^1], out var secs))
        {
            duration = secs;
            segments.RemoveAt(segments.Count - 1);
        }

        var artists = segments.Count > 0 ? segments[0] : "";
        var primaryArtist = detailRuns
            .Where(r => PageType(r) == "MUSIC_PAGE_TYPE_ARTIST")
            .Select(r => (string?)r?["text"])
            .FirstOrDefault() ?? artists.Split([", ", " & "], StringSplitOptions.None)[0];
        var album = detailRuns
            .Where(r => PageType(r) == "MUSIC_PAGE_TYPE_ALBUM")
            .Select(r => (string?)r?["text"])
            .FirstOrDefault();

        var thumbnail = item["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"]?.AsArray()
            .Select(t => (string?)t?["url"]).LastOrDefault(u => u is not null);
        if (thumbnail is not null)
            thumbnail = ThumbSize().Replace(thumbnail, "=w544-h544");

        return new MusicTrack(videoId, title, artists, primaryArtist, album, duration, thumbnail);
    }

    private static List<JsonNode?> Runs(JsonNode? column) =>
        column?["musicResponsiveListItemFlexColumnRenderer"]?["text"]?["runs"]?.AsArray().ToList() ?? [];

    private static string? PageType(JsonNode? run) =>
        (string?)run?["navigationEndpoint"]?["browseEndpoint"]?["browseEndpointContextSupportedConfigs"]
            ?["browseEndpointContextMusicConfig"]?["pageType"];

    public static bool TryParseDuration(string text, out int seconds)
    {
        seconds = 0;
        if (!DurationPattern().IsMatch(text)) return false;
        foreach (var part in text.Split(':'))
            seconds = seconds * 60 + int.Parse(part);
        return true;
    }

    private static IEnumerable<JsonNode> FindAll(JsonNode node, string key)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    if (child is null) continue;
                    if (name == key) yield return child;
                    else foreach (var found in FindAll(child, key)) yield return found;
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                    if (child is not null)
                        foreach (var found in FindAll(child, key)) yield return found;
                break;
        }
    }

    [GeneratedRegex(@"^\d{1,2}(:\d{2}){1,2}$")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"=w\d+-h\d+")]
    private static partial Regex ThumbSize();
}
