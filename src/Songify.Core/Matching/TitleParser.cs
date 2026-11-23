using System.Text.RegularExpressions;

namespace Songify.Core.Matching;

/// <param name="Artist">Best guess at the artist, or null if unknown.</param>
/// <param name="ArtistFromChannel">True when the artist was guessed from the channel name, not the title.</param>
public sealed record ParsedTitle(string? Artist, string Title, bool ArtistFromChannel)
{
    public string Query => Artist is null ? Title : $"{Artist} {Title}";
}

/// <summary>Turns a messy YouTube video title into artist + song title.</summary>
public static partial class TitleParser
{
    public static ParsedTitle Parse(string videoTitle, string? channel)
    {
        var cleaned = Clean(videoTitle);
        var parts = Separator().Split(cleaned)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();

        if (parts.Length >= 2)
            return new ParsedTitle(parts[0], StripQuotes(parts[1]), false);

        var title = StripQuotes(cleaned);
        var artist = CleanChannel(channel);
        return new ParsedTitle(artist, title, artist is not null);
    }

    /// <summary>Removes "(Official Video)", "[HD]", "| Lyrics", hashtags and similar noise.</summary>
    public static string Clean(string title)
    {
        var s = TextMatch.Brackets().Replace(title, m => IsNoise(m.Value) ? " " : m.Value);
        s = Hashtags().Replace(s, " ");
        s = UnbracketedNoise().Replace(s, " ");
        s = TextMatch.Spaces().Replace(s, " ").Trim();
        return TrailingSeparators().Replace(s, "").Trim();
    }

    public static string? CleanChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;
        var s = ChannelSuffix().Replace(channel, "").Trim();
        return s.Length == 0 ? null : s;
    }

    private static bool IsNoise(string bracketed)
    {
        var inner = TextMatch.Normalize(bracketed);
        if (inner.Length == 0) return true;
        if (VersionWords().IsMatch(inner)) return false;
        return NoiseWords().IsMatch(inner);
    }

    private static string StripQuotes(string s) => s.Trim().Trim('"', '“', '”', '\'', '‘', '’').Trim();

    [GeneratedRegex(@"\s+[-–—~]+\s+|\s+\|\s+")]
    private static partial Regex Separator();

    [GeneratedRegex(@"\b(official|video|audio|lyrics?|visuali[sz]er|hd|hq|4k|1080p|720p|mv|m v|clip|explicit|clean|letra|full song|out now|premiere|music video)\b")]
    private static partial Regex NoiseWords();

    // Bracketed text that may describe the recording; never strip these. Broader on purpose than
    // TrackMatcher.VersionWords, which lists only words that mean a *different* recording.
    [GeneratedRegex(@"\b(live|remix|mix|acoustic|instrumental|cover|edit|version|remaster(ed)?|sped|slowed|demo|extended|feat|ft|featuring|with|unplugged|session)\b")]
    private static partial Regex VersionWords();

    [GeneratedRegex(@"(?i)\b(official\s+(music\s+|lyric\s+)?(video|audio|visuali[sz]er)|lyrics?\s+video|music\s+video)\b")]
    private static partial Regex UnbracketedNoise();

    [GeneratedRegex(@"#\w+")]
    private static partial Regex Hashtags();

    [GeneratedRegex(@"[\s\-–—|~:]+$")]
    private static partial Regex TrailingSeparators();

    [GeneratedRegex(@"(?i)(\s*-\s*topic|vevo|\s+official)$")]
    private static partial Regex ChannelSuffix();
}
