using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Songify.Core.Audio;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Processing;
using Songify.Core.YouTube;

namespace Songify.App;

public enum TrackFilter { All, Review, Failed, Pending }

/// <summary>A combo box choice. ToString is what screen readers announce for the item.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 500;
    private static readonly string DefaultTemplate = new AppSettings().FileNameTemplate;

    private readonly SongifyService _service;
    private readonly ThemeSwitcher _themes;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private bool _loading;

    public ObservableCollection<TrackRecord> Tracks { get; } = [];
    public ICollectionView TracksView { get; }
    public ObservableCollection<string> Playlists { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public IReadOnlyList<Choice<AudioFormat>> Formats { get; } =
        [.. Enum.GetValues<AudioFormat>().OrderBy(f => f != AudioFormat.Original).Select(f => new Choice<AudioFormat>(f, f.DisplayName()))];
    public IReadOnlyList<int> Bitrates { get; } = AudioFormatInfo.Bitrates;
    public IReadOnlyList<int> PollIntervals { get; } = [5, 10, 15, 30, 60];
    public IReadOnlyList<int> IdleMinuteOptions { get; } = [2, 5, 10, 15, 30];
    public IReadOnlyList<Choice<AppTheme>> ThemeOptions { get; } =
    [
        new(AppTheme.System, "Match Windows"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.Dark, "Dark"),
    ];

    [ObservableProperty] private string _newPlaylistUrl = "";
    [ObservableProperty] private string? _playlistError;
    [ObservableProperty] private string _outputFolder = "";
    [ObservableProperty] private string? _outputFolderError;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBitrate), nameof(BitrateNote), nameof(FormatHint))]
    private AudioFormat _selectedFormat;
    [ObservableProperty] private int _bitrate;
    [ObservableProperty] private string _fileNameTemplate = "";
    [ObservableProperty] private bool _monitoringEnabled;
    [ObservableProperty] private int _pollIntervalMinutes;
    [ObservableProperty] private bool _allowVideoFallback;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FormatHint))] private bool _recordFromSpotify;
    [ObservableProperty] private bool _spotifyOnlyWhenIdle;
    [ObservableProperty] private int _spotifyIdleMinutes;
    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private TrackFilter _filter = TrackFilter.All;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _savedCount;
    [ObservableProperty] private int _reviewCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RetrySelectedCommand))]
    private IReadOnlyList<TrackRecord> _selectedTracks = [];

    public bool ShowBitrate => SelectedFormat.UsesBitrate();

    /// <summary>Replaces the bitrate box when the format has no bitrate to choose.</summary>
    public string? BitrateNote =>
        SelectedFormat.UsesBitrate() ? null
        : SelectedFormat == AudioFormat.Original ? "Same as source"
        : "Not used (lossless)";

    /// <summary>Shown under the bitrate and read by screen readers as the Format box's help text.</summary>
    public string FormatHint =>
        SelectedFormat == AudioFormat.Original
            ? "Keeps YouTube's audio exactly as downloaded (usually Opus, about 130–160 kbps), so there's no bitrate to pick: no quality loss and the smallest files. Some car stereos and iTunes can't play .opus files."
              + (RecordFromSpotify ? $" Spotify recordings are saved as Ogg Vorbis {AudioEncoder.SpotifyFreeKbps} kbps, Spotify Free's own quality." : "")
        : SelectedFormat.IsLossless()
            ? "Lossless has no bitrate to pick. Files are 5–10 times larger and can't sound better than YouTube's audio."
            : "YouTube audio is about 130–160 kbps, so above 256 kbps mostly adds file size. Converting always loses a little quality; Original doesn't.";
    public bool HasNoPlaylists => Playlists.Count == 0;
    public bool HasNoTracks => Tracks.Count == 0;

    public MainViewModel(SongifyService service, ThemeSwitcher themes, Dispatcher dispatcher)
    {
        _service = service;
        _themes = themes;
        _dispatcher = dispatcher;

        _loading = true;
        var s = service.Settings;
        OutputFolder = s.OutputFolder;
        SelectedFormat = s.Format;
        Bitrate = s.BitrateKbps;
        FileNameTemplate = s.FileNameTemplate;
        MonitoringEnabled = s.MonitoringEnabled;
        PollIntervalMinutes = s.PollIntervalMinutes;
        AllowVideoFallback = s.AllowVideoFallback;
        RecordFromSpotify = s.RecordFromSpotify;
        SpotifyOnlyWhenIdle = s.SpotifyOnlyWhenIdle;
        // A hand-edited value outside the list would leave the box blank: show the nearest choice.
        SpotifyIdleMinutes = IdleMinuteOptions.MinBy(m => Math.Abs(m - s.SpotifyIdleMinutes));
        Theme = s.Theme;
        foreach (var url in s.Playlists) Playlists.Add(url);
        _loading = false;

        foreach (var track in service.GetTracks()) AddTrack(track);
        TracksView = CollectionViewSource.GetDefaultView(Tracks);
        TracksView.Filter = o => o is TrackRecord t && Matches(t, Filter);
        Playlists.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoPlaylists));
        Tracks.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoTracks));

        // Track changes arrive from worker threads; batch UI refreshes.
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Refresh(), dispatcher);
        _refreshTimer.Stop();
        RecountNow();

        service.TrackAdded += t => _dispatcher.BeginInvoke(() => { AddTrack(t); ScheduleRefresh(); });
        service.StatusChanged += text => _dispatcher.BeginInvoke(() => StatusText = text);
        AppLog.Line += line => _dispatcher.BeginInvoke(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
        });
    }

    private void AddTrack(TrackRecord track)
    {
        Tracks.Insert(0, track); // newest first
        track.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TrackRecord.Status) or nameof(TrackRecord.NeedsReview))
                _dispatcher.BeginInvoke(ScheduleRefresh);
        };
    }

    private void ScheduleRefresh()
    {
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    private void Refresh()
    {
        _refreshTimer.Stop();
        RecountNow();
        if (Filter != TrackFilter.All) TracksView.Refresh();
        RetrySelectedCommand.NotifyCanExecuteChanged();
    }

    private void RecountNow()
    {
        TotalCount = Tracks.Count;
        SavedCount = Tracks.Count(t => t.Status == TrackStatus.Done);
        ReviewCount = Tracks.Count(t => Matches(t, TrackFilter.Review));
        FailedCount = Tracks.Count(t => t.Status == TrackStatus.Failed);
        PendingCount = Tracks.Count(t => Matches(t, TrackFilter.Pending));
    }

    private static bool Matches(TrackRecord t, TrackFilter filter) => filter switch
    {
        TrackFilter.Review => t.Status == TrackStatus.Done && t.NeedsReview,
        TrackFilter.Failed => t.Status == TrackStatus.Failed,
        TrackFilter.Pending => t.IsPending,
        _ => true,
    };

    partial void OnFilterChanged(TrackFilter value) => TracksView.Refresh();

    // Settings: every edit is applied immediately.
    partial void OnOutputFolderChanged(string value)
    {
        if (!_loading && (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)))
        {
            OutputFolder = _service.Settings.OutputFolder; // re-enters with a valid value (and clears the error)
            OutputFolderError = "Use a full folder path, like C:\\Users\\you\\Music\\Songify. Kept the previous folder.";
            return;
        }
        if (Path.IsPathFullyQualified(value)) OutputFolderError = null;
        PushSettings();
    }

    partial void OnSelectedFormatChanged(AudioFormat value) => PushSettings();
    partial void OnBitrateChanged(int value) => PushSettings();
    partial void OnMonitoringEnabledChanged(bool value) => PushSettings();
    partial void OnPollIntervalMinutesChanged(int value) => PushSettings();
    partial void OnAllowVideoFallbackChanged(bool value) => PushSettings();
    partial void OnRecordFromSpotifyChanged(bool value) => PushSettings();
    partial void OnSpotifyOnlyWhenIdleChanged(bool value) => PushSettings();
    partial void OnSpotifyIdleMinutesChanged(int value) => PushSettings();

    partial void OnThemeChanged(AppTheme value)
    {
        if (_loading) return; // App already applied the saved theme
        _themes.Apply(value);
        PushSettings();
    }

    partial void OnFileNameTemplateChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) && !_loading)
        {
            FileNameTemplate = DefaultTemplate; // re-enters with a valid value
            return;
        }
        PushSettings();
    }

    private void PushSettings()
    {
        if (_loading) return;
        _service.UpdateSettings(s =>
        {
            s.OutputFolder = OutputFolder;
            s.Format = SelectedFormat;
            s.BitrateKbps = Bitrate;
            s.FileNameTemplate = FileNameTemplate;
            s.MonitoringEnabled = MonitoringEnabled;
            s.PollIntervalMinutes = PollIntervalMinutes;
            s.AllowVideoFallback = AllowVideoFallback;
            s.RecordFromSpotify = RecordFromSpotify;
            s.SpotifyOnlyWhenIdle = SpotifyOnlyWhenIdle;
            s.SpotifyIdleMinutes = SpotifyIdleMinutes;
            s.Theme = Theme;
            s.Playlists = [.. Playlists];
        });
    }

    [RelayCommand]
    private void AddPlaylist()
    {
        var url = NewPlaylistUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !YouTubeUrls.IsYouTubeHost(uri.Host))
        {
            PlaylistError = "Paste a YouTube link, like https://www.youtube.com/playlist?list=…";
            return;
        }
        if (Playlists.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            PlaylistError = "That playlist is already in the list.";
            return;
        }
        PlaylistError = null;
        Playlists.Add(url);
        NewPlaylistUrl = "";
        PushSettings();
        _service.CheckNow();
    }

    partial void OnNewPlaylistUrlChanged(string value) => PlaylistError = null;

    [RelayCommand]
    private void RemovePlaylist(string url)
    {
        Playlists.Remove(url);
        PushSettings();
    }

    [RelayCommand]
    private void CheckNow() => _service.CheckNow();

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where Songify saves songs",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : null,
        };
        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        Directory.CreateDirectory(OutputFolder);
        Open(OutputFolder);
    }

    // Failed only: re-running a saved track would leave its old file behind as a "(2)" duplicate.
    private bool CanRetrySelected() => SelectedTracks.Any(t => t.Status == TrackStatus.Failed);

    [RelayCommand(CanExecute = nameof(CanRetrySelected))]
    private void RetrySelected()
    {
        foreach (var track in SelectedTracks) _service.Retry(track);
    }

    [RelayCommand]
    private void ShowInFolder(TrackRecord? track)
    {
        if (track?.FilePath is { } path && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenOriginalVideo(TrackRecord? track)
    {
        if (track is not null) Open(YouTubeUrls.Video(track.VideoId));
    }

    [RelayCommand]
    private void OpenSavedSource(TrackRecord? track)
    {
        if (track?.MatchedVideoId is { } id) Open(YouTubeUrls.For(track.Source, id));
    }

    private static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
