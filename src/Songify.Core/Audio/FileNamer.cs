using System.Globalization;
using System.Text.RegularExpressions;
using Songify.Core.Matching;

namespace Songify.Core.Audio;

public static partial class FileNamer
{
    private const int MaxBaseNameLength = 150;

    /// <summary>
    /// Fills the template ({artist}, {title}, {album}, {year}, {id}) and makes it a safe file name.
    /// {artist} is the primary artist; featured artists are usually in the title already.
    /// </summary>
    public static string BuildBaseName(string template, TrackTags tags, string videoId)
    {
        var name = template
            .Replace("{artist}", tags.AlbumArtist ?? tags.Artist ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", tags.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{album}", tags.Album ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{year}", tags.Year?.ToString(CultureInfo.InvariantCulture) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{id}", videoId, StringComparison.OrdinalIgnoreCase);
        // An empty token in brackets, e.g. "({year})" with no year, would leave "()".
        name = EmptyBrackets().Replace(name, "");
        name = Sanitize(name);
        // An empty token can leave "- Title" or "Artist -".
        name = EdgeJunk().Replace(name, "");
        return name.Length == 0 ? videoId : name;
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = TextMatch.Spaces().Replace(new string(chars), " ").Trim().TrimEnd('.');
        if (s.Length > MaxBaseNameLength) s = s[..MaxBaseNameLength].TrimEnd();
        // Windows reserves CON, NUL, COM1… even with an extension.
        return ReservedName().IsMatch(s) ? "_" + s : s;
    }

    /// <summary>Returns folder\baseName.ext, or folder\baseName (2).ext etc. if taken.</summary>
    public static string UniquePath(string folder, string baseName, string extension)
    {
        var path = Path.Combine(folder, baseName + extension);
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{baseName} ({i}){extension}");
        return path;
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    [GeneratedRegex(@"^[\s\-–.]+|[\s\-–]+$")]
    private static partial Regex EdgeJunk();

    [GeneratedRegex(@"\(\s*\)|\[\s*\]")]
    private static partial Regex EmptyBrackets();
}
