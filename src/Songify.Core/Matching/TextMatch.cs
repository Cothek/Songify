using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Songify.Core.Matching;

/// <summary>String normalization and fuzzy similarity for song titles and artist names.</summary>
public static partial class TextMatch
{
    /// <summary>Lowercase, strip accents and punctuation, "&amp;" → "and", collapse spaces.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\'' or '’' or '`') continue; // "don't" == "dont"
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>
    /// Title with bracketed parts, " - suffix" (e.g. "- Remastered 2011") and featuring credits removed.
    /// </summary>
    public static string CoreTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var s = Brackets().Replace(title, " ");
        var dash = DashSeparator().Match(s);
        if (dash.Success && dash.Index > 0) s = s[..dash.Index];
        s = FeaturingTail().Replace(s, "");
        return Normalize(s);
    }

    /// <summary>0..1; the better of character-level and word-level similarity of normalized strings.</summary>
    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a);
        var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return 0;
        if (x == y) return 1;
        return Math.Max(LevenshteinRatio(x, y), TokenDice(x, y));
    }

    public static double LevenshteinRatio(string a, string b)
    {
        var max = Math.Max(a.Length, b.Length);
        return max == 0 ? 1 : 1.0 - (double)Levenshtein(a, b) / max;
    }

    public static double TokenDice(string a, string b)
    {
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var common = ta.Count(tb.Contains);
        return 2.0 * common / (ta.Count + tb.Count);
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    [GeneratedRegex(@"\s+")]
    internal static partial Regex Spaces();

    [GeneratedRegex(@"[\(\[\{【][^\)\]\}】]*[\)\]\}】]")]
    internal static partial Regex Brackets();

    [GeneratedRegex(@"\s[-–—]\s")]
    private static partial Regex DashSeparator();

    [GeneratedRegex(@"\s(feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    internal static partial Regex FeaturingTail();
}
