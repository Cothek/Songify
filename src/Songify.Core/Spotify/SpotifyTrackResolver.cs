using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Songify.Core.Infrastructure;
using Songify.Core.Matching;
using Songify.Core.YouTube;
using static Songify.Core.Infrastructure.JsonValues;

namespace Songify.Core.Spotify;

public sealed record SpotifyMatch(SpotifyTrack Track, double Score);

/// <summary>
/// Finds the Spotify track for a song without the Spotify API (which Free accounts can't use):
/// Deezer search (keyless) gives the recording's ISRC, MusicBrainz maps the ISRC to Spotify links,
/// and Spotify's oEmbed endpoint confirms the link is live. MusicBrainz has Spotify links for many
/// popular songs but far from all, so a miss is common.
/// </summary>
public sealed partial class SpotifyTrackResolver(HttpClient http)
{
    private const int CandidatesToTry = 3;
    private const double SameTitleThreshold = 0.8;
    private const string MusicBrainzAgent = "Songify/1 ( https://github.com/Cothek/Songify )";

    // MusicBrainz allows one request per second per client.
    private static readonly TimeSpan MusicBrainzInterval = TimeSpan.FromSeconds(1.1);
    private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
    private static DateTimeOffset _lastMusicBrainz = DateTimeOffset.MinValue;

    public sealed record DeezerTrack(
        string Title, string Artist, string? Album, long? AlbumId, int? DurationSeconds, string? Isrc, string? CoverUrl)
    {
        public MusicTrack ForScoring() => new("", Title, Artist, Artist, Album, DurationSeconds, CoverUrl);
    }

    /// <summary>Null when the song isn't on Deezer, scores below <paramref name="minScore"/>, or has no known Spotify link.</summary>
    public async Task<SpotifyMatch?> FindAsync(ParsedTitle song, int? durationSeconds, double minScore, CancellationToken ct)
    {
        var json = await GetStringAsync($"https://api.deezer.com/search?q={Uri.EscapeDataString(song.Query)}&limit=10", null, ct);
        var candidates = ParseDeezerSearch(json ?? "")
            .Select(t => (Track: t, Score: TrackMatcher.Score(song, durationSeconds, t.ForScoring())))
            .Where(c => c.Score >= minScore && c.Track.Isrc is not null)
            .OrderByDescending(c => c.Score)
            .Take(CandidatesToTry);

        foreach (var (candidate, score) in candidates)
        {
            foreach (var id in await SpotifyIdsForIsrcAsync(candidate.Isrc!, ct))
            {
                if (!await IsLiveTrackAsync(id, candidate.Title, ct)) continue;
                var year = await AlbumYearAsync(candidate.AlbumId, ct);
                var track = new SpotifyTrack(id, candidate.Title, candidate.Artist, candidate.Album, year,
                    candidate.DurationSeconds, candidate.CoverUrl);
                return new SpotifyMatch(track, score);
            }
        }
        return null;
    }

    /// <summary>
    /// True when what Spotify reports as playing is <paramref name="track"/>. Spotify's titles can differ
    /// from Deezer's ("Song - Radio Edit" vs "Song (Radio Edit)"), so compare core titles and lead artists.
    /// </summary>
    public static bool IsSameSong(SpotifyTrack track, string? playingTitle, string? playingArtist)
    {
        if (string.IsNullOrWhiteSpace(playingTitle) || !IsSameTitle(track.Title, playingTitle)) return false;
        return string.IsNullOrWhiteSpace(playingArtist)
               || TrackMatcher.ArtistSimilarity(track.Artist, playingArtist) >= SameTitleThreshold
               || TrackMatcher.ArtistSimilarity(playingArtist, track.Artist) >= SameTitleThreshold;
    }

    public static IReadOnlyList<DeezerTrack> ParseDeezerSearch(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["data"] is not JsonArray data) return [];
        return data.OfType<JsonObject>()
            .Select(t => new DeezerTrack(
                Title: Text(t, "title") ?? "",
                Artist: Text(t["artist"], "name") ?? "",
                Album: Text(t["album"], "title"),
                AlbumId: Number(t["album"], "id") is double id ? (long)id : null,
                DurationSeconds: Number(t, "duration") is double secs ? (int)Math.Round(secs) : null,
                Isrc: Text(t, "isrc"),
                CoverUrl: Text(t["album"], "cover_xl")))
            .Where(t => t.Title.Length > 0 && t.Artist.Length > 0)
            .ToList();
    }

    /// <summary>Spotify track IDs linked from the MusicBrainz recordings with this ISRC.</summary>
    public static IReadOnlyList<string> ParseSpotifyIds(string musicBrainzJson)
    {
        if (JsonNode.Parse(musicBrainzJson) is not JsonObject root || root["recordings"] is not JsonArray recordings) return [];
        return recordings.OfType<JsonObject>()
            .SelectMany(r => r["relations"] as JsonArray ?? [])
            .Select(rel => Text(rel?["url"], "resource"))
            .Select(url => url is null ? null : SpotifyTrackUrl().Match(url))
            .Where(m => m is { Success: true })
            .Select(m => m!.Groups[1].Value)
            .Distinct()
            .ToList();
    }

    private async Task<IReadOnlyList<string>> SpotifyIdsForIsrcAsync(string isrc, CancellationToken ct)
    {
        await MusicBrainzGate.WaitAsync(ct);
        try
        {
            var wait = _lastMusicBrainz + MusicBrainzInterval - DateTimeOffset.Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var json = await GetStringAsync(
                $"https://musicbrainz.org/ws/2/isrc/{Uri.EscapeDataString(isrc)}?inc=url-rels&fmt=json", MusicBrainzAgent, ct);
            return json is null ? [] : ParseSpotifyIds(json);
        }
        finally
        {
            _lastMusicBrainz = DateTimeOffset.Now;
            MusicBrainzGate.Release();
        }
    }

    /// <summary>MusicBrainz links can be stale; oEmbed only answers for tracks that still exist.</summary>
    private async Task<bool> IsLiveTrackAsync(string id, string expectedTitle, CancellationToken ct)
    {
        var json = await GetStringAsync($"https://open.spotify.com/oembed?url={Uri.EscapeDataString(SpotifyTrack.Url(id))}", null, ct);
        var title = json is null ? null : Text(JsonNode.Parse(json), "title");
        return title is not null && IsSameTitle(title, expectedTitle);
    }

    /// <summary>Same song title, ignoring "(feat. …)", "- Radio Edit" and similar suffixes.</summary>
    private static bool IsSameTitle(string a, string b) =>
        TextMatch.Similarity(TextMatch.CoreTitle(a), TextMatch.CoreTitle(b)) >= SameTitleThreshold;

    /// <summary>The release year is a nice-to-have tag: any failure just means no year.</summary>
    private async Task<int?> AlbumYearAsync(long? albumId, CancellationToken ct)
    {
        if (albumId is null) return null;
        try
        {
            var json = await GetStringAsync($"https://api.deezer.com/album/{albumId}", null, ct);
            var date = json is null ? null : Text(JsonNode.Parse(json), "release_date");
            return date is { Length: >= 4 } && int.TryParse(date[..4], out var year) && year is > 1000 and < 3000 ? year : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Waits before retrying a "busy" answer (MusicBrainz sends 503 when overloaded or rate-limited).</summary>
    private static readonly TimeSpan[] BusyRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    /// <summary>Body of a 2xx response, null for 404. Other failures throw, so an outage isn't mistaken for "not on Spotify".</summary>
    private async Task<string?> GetStringAsync(string url, string? userAgent, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (userAgent is not null) request.Headers.UserAgent.ParseAdd(userAgent);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
                if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests
                    && attempt < BusyRetryDelays.Length)
                {
                    await Task.Delay(BusyRetryDelays[attempt], ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(timeout.Token);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            AppLog.Warn($"Spotify lookup request failed: {new Uri(url).Host} ({ex.Message})");
            throw new InvalidOperationException($"Spotify song lookup failed ({ex.Message}). Try again later.", ex);
        }
    }

    [GeneratedRegex(@"^https?://open\.spotify\.com/track/([A-Za-z0-9]{22})")]
    private static partial Regex SpotifyTrackUrl();
}
