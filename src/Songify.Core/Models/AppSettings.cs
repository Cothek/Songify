namespace Songify.Core.Models;

/// <summary>
/// User settings. Treated as an immutable snapshot once handed to the service:
/// the UI applies edits through <c>SongifyService.UpdateSettings</c>.
/// </summary>
public sealed class AppSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Songify");

    public AudioFormat Format { get; set; } = AudioFormat.Mp3;
    public int BitrateKbps { get; set; } = 320;

    /// <summary>Tokens: {artist}, {title}, {album}, {year}, {id}.</summary>
    public string FileNameTemplate { get; set; } = "{artist} - {title}";

    public List<string> Playlists { get; set; } = [];
    public bool MonitoringEnabled { get; set; } = true;
    public int PollIntervalMinutes { get; set; } = 10;

    /// <summary>Use the YouTube video's own audio when no YouTube Music album track matches.</summary>
    public bool AllowVideoFallback { get; set; } = true;

    /// <summary>
    /// Before falling back to the video's audio, record the song from the Spotify desktop app.
    /// Off by default: it plays songs in real time, mutes the PC while recording, and breaks Spotify's terms.
    /// </summary>
    public bool RecordFromSpotify { get; set; }

    /// <summary>Record only when nobody has used the PC for <see cref="SpotifyIdleMinutes"/> and no other app is playing sound.</summary>
    public bool SpotifyOnlyWhenIdle { get; set; } = true;

    public int SpotifyIdleMinutes { get; set; } = 5;

    /// <summary>Minimum match score (0..1) to accept a YouTube Music candidate.</summary>
    public double MinMatchScore { get; set; } = 0.70;

    public int MaxParallelDownloads { get; set; } = 2;

    public AppTheme Theme { get; set; } = AppTheme.System;

    public DateTimeOffset? LastToolUpdateCheck { get; set; }

    /// <summary>
    /// Resets values a hand-edited file can make invalid (enums also load from bare numbers).
    /// Returns this for chaining after load.
    /// </summary>
    public AppSettings Sanitize()
    {
        var defaults = new AppSettings();
        if (!Enum.IsDefined(Format)) Format = defaults.Format;
        if (!Enum.IsDefined(Theme)) Theme = defaults.Theme;
        if (SpotifyIdleMinutes < 1) SpotifyIdleMinutes = defaults.SpotifyIdleMinutes;
        return this;
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Playlists = [.. Playlists];
        return copy;
    }
}
