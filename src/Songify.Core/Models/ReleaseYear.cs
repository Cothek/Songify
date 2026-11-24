using System.Globalization;

namespace Songify.Core.Models;

/// <summary>Release years from tags, APIs and user input; anything implausible is treated as unknown.</summary>
public static class ReleaseYear
{
    public const int Min = 1000, Max = 2999;

    public static bool IsPlausible(int year) => year is >= Min and <= Max;

    public static int? From(double? value) =>
        value is { } v && v == Math.Floor(v) && v >= Min && v <= Max ? (int)v : null;

    /// <summary>The year at the start of a date such as "2013", "2013-05-17" or "2013/05".</summary>
    public static int? FromDate(string? date) =>
        date?.Trim() is { Length: >= 4 } d && int.TryParse(d.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && (d.Length == 4 || !char.IsDigit(d[4])) && IsPlausible(year)
            ? year
            : null;
}
