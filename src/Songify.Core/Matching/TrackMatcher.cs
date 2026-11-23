using System.Text.RegularExpressions;
using Songify.Core.YouTube;

namespace Songify.Core.Matching;

public sealed record MatchResult(MusicTrack Track, double Score);

/// <summary>Scores YouTube Music candidates against a parsed YouTube video title.</summary>
public static partial class TrackMatcher
{
    private const double TitleWeight = 0.55;
    private const double ArtistWeight = 0.30;
    private const double DurationWeight = 0.15;
    private const double VersionMismatchPenalty = 0.30;
    private const double SwappedPenalty = 0.05;

    // Without artist evidence a same-titled song by someone else would pass (many songs are called "Hello").
    private const double WeakArtistThreshold = 0.5;
    private const double WeakArtistPenalty = 0.2;

    // "Love" vs "Love Story" share words but are different songs.
    private const double TitleFloor = 0.8;
    private const double TitleFloorPenalty = 0.3;

    public static MatchResult? Best(ParsedTitle source, int? sourceDuration, IEnumerable<MusicTrack> candidates) =>
        candidates
            .Select(c => new MatchResult(c, Score(source, sourceDuration, c)))
            .OrderByDescending(r => r.Score)
            .FirstOrDefault();

    public static double Score(ParsedTitle source, int? sourceDuration, MusicTrack candidate)
    {
        var duration = DurationScore(sourceDuration, candidate.DurationSeconds);
        var score = Combine(source.Artist, source.Title, candidate, duration);

        // Some uploads are "Title - Artist".
        if (source.Artist is not null && !source.ArtistFromChannel)
            score = Math.Max(score, Combine(source.Title, source.Artist, candidate, duration) - SwappedPenalty);

        // "Live", "Remix", "Acoustic"… on one side only means a different recording.
        // Title only: an artist named e.g. "Live" must not count.
        var mismatches = VersionTags(source.Title);
        mismatches.SymmetricExceptWith(VersionTags($"{candidate.Title} {candidate.Album}"));
        score -= mismatches.Count * VersionMismatchPenalty;

        return Math.Clamp(score, 0, 1);
    }

    private static double Combine(string? artist, string title, MusicTrack candidate, double duration)
    {
        var titleSim = TextMatch.Similarity(TextMatch.CoreTitle(title), TextMatch.CoreTitle(candidate.Title));
        var artistSim = artist is null ? 0 : ArtistSimilarity(artist, candidate.Artists);
        var score = TitleWeight * titleSim + ArtistWeight * artistSim + DurationWeight * duration;
        if (artistSim < WeakArtistThreshold) score -= WeakArtistPenalty;
        if (titleSim < TitleFloor) score -= TitleFloorPenalty;
        return score;
    }

    /// <summary>
    /// 1 when the source artist equals the candidate credit or one of its names; otherwise the best
    /// character-level similarity. Character-level (not word overlap) so "Queen" ≠ "Queen Latifah".
    /// </summary>
    public static double ArtistSimilarity(string sourceArtist, string candidateArtists)
    {
        var sourceFull = TextMatch.Normalize(sourceArtist);
        var candidateFull = TextMatch.Normalize(candidateArtists);
        if (sourceFull.Length == 0 || candidateFull.Length == 0) return 0;

        var candidateNames = SplitArtists(candidateArtists, ArtistSplit()).Append(candidateFull).ToList();
        if (candidateNames.Contains(sourceFull)) return 1;

        // "feat."/"x"/"vs" always separate artists, so the name before them is the artist.
        var lead = SplitArtists(sourceArtist, FeaturingSplit()).FirstOrDefault() ?? sourceFull;
        // "," / "&" / "and" may be part of a band name ("Earth, Wind & Fire"), so a match on just
        // the first part is likely but not certain.
        var first = SplitArtists(sourceArtist, ArtistSplit()).FirstOrDefault() ?? sourceFull;
        return Math.Max(
            Math.Max(
                candidateNames.Max(n => TextMatch.LevenshteinRatio(lead, n)),
                AmbiguousPartCap * candidateNames.Max(n => TextMatch.LevenshteinRatio(first, n))),
            TextMatch.LevenshteinRatio(sourceFull, candidateFull));
    }

    private const double AmbiguousPartCap = 0.8;

    private static IEnumerable<string> SplitArtists(string artists, Regex separators) =>
        separators.Split(artists).Select(TextMatch.Normalize).Where(n => n.Length > 0);

    /// <summary>
    /// 1 when lengths agree within 5s. A longer candidate is a strong mismatch (falls to 0 at +30s);
    /// a shorter one is tolerated because music videos often add intros/outros (falls to 0 at -180s).
    /// </summary>
    public static double DurationScore(int? source, int? candidate)
    {
        if (source is null || candidate is null || source <= 0) return 0.5;
        var diff = candidate.Value - source.Value;
        if (Math.Abs(diff) <= 5) return 1;
        return diff > 0
            ? Math.Max(0, 1 - (diff - 5) / 25.0)
            : Math.Max(0, 1 - (-diff - 5) / 175.0);
    }

    public static HashSet<string> VersionTags(string? text)
    {
        var normalized = TextMatch.Normalize(text);
        return VersionWords().Matches(normalized)
            .Select(m => m.Value.Contains("cappella") || m.Value == "acapella" ? "a cappella" : m.Value)
            .ToHashSet();
    }

    [GeneratedRegex(@"\b(live|remix|acoustic|instrumental|karaoke|cover|sped up|slowed|nightcore|8d|demo|extended|a ?cappella|acapella|orchestral|mashup|bootleg|rework|unplugged)\b")]
    private static partial Regex VersionWords();

    [GeneratedRegex(@"\s*(?:,|&|\band\b|\bx\b|\bvs\.?|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b|/)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSplit();

    [GeneratedRegex(@"\s*(?:\bx\b|\bvs\.?|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b|/)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingSplit();
}
