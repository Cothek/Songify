using System.Text.RegularExpressions;
using Songify.Core.Models;

namespace Songify.Core.YouTube;

public static partial class YouTubeUrls
{
    public static string Video(string id) => $"https://www.youtube.com/watch?v={id}";
    public static string Music(string id) => $"https://music.youtube.com/watch?v={id}";

    /// <summary>Web link for a saved track's <see cref="TrackRecord.MatchedVideoId"/>.</summary>
    public static string For(TrackSource source, string id) => source switch
    {
        TrackSource.YouTubeMusic => Music(id),
        TrackSource.Spotify => Spotify.SpotifyTrack.Url(id),
        _ => Video(id),
    };

    /// <summary>Thumbnail URLs to try, best first (maxres doesn't exist for every video).</summary>
    public static IReadOnlyList<string> Thumbnails(string id) =>
    [
        $"https://i.ytimg.com/vi/{id}/maxresdefault.jpg",
        $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
    ];

    public static bool IsVideoId(string? id) => id is not null && VideoIdPattern().IsMatch(id);

    public static bool IsYouTubeHost(string host) =>
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();
}
