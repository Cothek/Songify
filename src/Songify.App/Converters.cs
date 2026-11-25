using System.Globalization;
using System.Windows.Data;
using Songify.Core.Models;

namespace Songify.App;

/// <summary>(Status, NeedsReview) → user-facing status label.</summary>
public sealed class StatusLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not TrackStatus status) return "";
        var review = values[1] is true;
        return status switch
        {
            TrackStatus.Queued => "Waiting",
            TrackStatus.Matching => "Finding match",
            TrackStatus.Downloading => "Downloading",
            TrackStatus.Encoding => "Converting",
            TrackStatus.WaitingToRecord => "Waiting to record",
            TrackStatus.Recording => "Recording",
            TrackStatus.Done => review ? "Saved – check" : "Saved",
            TrackStatus.Failed => "Failed",
            _ => status.ToString(),
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SourceLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        TrackSource.YouTubeMusic => "YouTube Music",
        TrackSource.YouTubeVideo => "Video audio",
        TrackSource.Spotify => "Spotify recording",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? $"{d:P0}" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Binds the second of two RadioButtons to the same bool as the first.</summary>
public sealed class InverseBool : IValueConverter
{
    public static readonly InverseBool Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Binds a RadioButton's IsChecked to "enum property equals ConverterParameter".</summary>
public sealed class EnumMatch : IValueConverter
{
    public static readonly EnumMatch Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}
