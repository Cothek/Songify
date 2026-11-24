using System.ComponentModel;
using System.Threading.Channels;
using Songify.Core.Audio;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Spotify;
using Songify.Core.Tools;
using Songify.Core.YouTube;

namespace Songify.Core.Processing;

/// <summary>
/// Owns settings and the track library, polls playlists for new videos, and runs the
/// download workers. Thread-safe; events fire on background threads.
/// </summary>
public sealed class SongifyService : IAsyncDisposable
{
    private static readonly TimeSpan YtDlpUpdateInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(10);

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly ToolManager _tools;
    private readonly YtDlpClient _ytDlp;
    private readonly TrackProcessor _processor;

    private readonly object _gate = new();          // guards _tracks, _byId, _enqueued, _running
    private readonly object _settingsGate = new();  // guards settings read-modify-write and its file
    private readonly List<TrackRecord> _tracks;
    private readonly Dictionary<string, TrackRecord> _byId;
    private readonly HashSet<string> _enqueued = [];
    private readonly Channel<TrackRecord> _queue = Channel.CreateUnbounded<TrackRecord>();
    private readonly Channel<SpotifyJob> _recordings = Channel.CreateUnbounded<SpotifyJob>(); // one recorder
    private readonly SemaphoreSlim _pollSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _saveTimer;
    private readonly List<Task> _running = [];
    private volatile AppSettings _settings;
    private volatile bool _manualCheckRequested;
    private volatile bool _disposed;
    private int _savePending;

    public event Action<TrackRecord>? TrackAdded;
    public event Action<string>? StatusChanged;

    public AppSettings Settings => _settings;
    public bool ToolsReady { get; private set; }

    /// <param name="spotifyRecorder">Null where Spotify recording isn't available; the setting then has no effect.</param>
    public SongifyService(AppPaths paths, ISpotifyRecorder? spotifyRecorder = null)
    {
        _paths = paths;
        _paths.EnsureCreated();
        AppLog.FilePath = paths.LogFile;

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
        spotifyRecorder?.RecoverFromCrash(); // after logging is set up
        _tools = new ToolManager(paths.ToolsDir, _http);
        _ytDlp = new YtDlpClient(_tools);
        _processor = new TrackProcessor(_ytDlp, new YouTubeMusicClient(_http), new SpotifyTrackResolver(_http), spotifyRecorder,
            new AudioEncoder(_tools), _http, paths.WorkDir);

        _settings = (JsonFile.Load<AppSettings>(paths.SettingsFile) ?? new AppSettings()).Sanitize();
        _tracks = (JsonFile.Load<List<TrackRecord>>(paths.LibraryFile) ?? [])
            .Where(t => !string.IsNullOrEmpty(t.VideoId))
            .DistinctBy(t => t.VideoId)
            .ToList();
        _byId = _tracks.ToDictionary(t => t.VideoId);
        foreach (var track in _tracks)
        {
            // Work interrupted by a previous exit starts over.
            if (track.IsInProgress) track.Status = TrackStatus.Queued;
            track.PropertyChanged += OnTrackChanged;
        }
        _saveTimer = new Timer(_ => SaveLibrary(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IReadOnlyList<TrackRecord> GetTracks()
    {
        lock (_gate) return [.. _tracks];
    }

    /// <summary>Downloads missing tools, then starts the workers and the playlist monitor.</summary>
    public async Task StartAsync()
    {
        var ct = _cts.Token;
        try
        {
            ClearWorkDir();
            await _tools.EnsureAsync(new Progress<string>(Report), ct);
            await UpdateYtDlpIfDueAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            AppLog.Error("Could not download the tools Songify needs (yt-dlp, ffmpeg, Deno). Check your internet connection and restart Songify", ex);
            Report("Tool download failed. See the activity log.");
            return;
        }
        ToolsReady = true;

        lock (_gate)
        {
            if (_disposed) return;
            for (var i = 0; i < Math.Max(1, _settings.MaxParallelDownloads); i++)
                _running.Add(Task.Run(() => WorkerAsync(ct)));
            _running.Add(Task.Run(() => RecorderAsync(ct)));
            _running.Add(Task.Run(() => MonitorAsync(ct)));
        }
        foreach (var track in GetTracks().Where(t => t.Status == TrackStatus.Queued))
            Enqueue(track);
        Report("Ready");
    }

    /// <summary>Atomically edits a copy of the settings, swaps it in, and saves it.</summary>
    public void UpdateSettings(Action<AppSettings> edit)
    {
        AppSettings previous, next;
        lock (_settingsGate)
        {
            previous = _settings;
            next = previous.Clone();
            edit(next);
            _settings = next;
            try
            {
                JsonFile.Save(_paths.SettingsFile, next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("Could not save settings", ex);
            }
        }
        if (previous.PollIntervalMinutes != next.PollIntervalMinutes || previous.MonitoringEnabled != next.MonitoringEnabled)
            _pollSignal.Release(); // re-arm the wait with the new interval
    }

    public void CheckNow()
    {
        _manualCheckRequested = true;
        _pollSignal.Release();
    }

    /// <summary>Re-queues a failed track. Ignored for tracks in any other state.</summary>
    public void Retry(TrackRecord track)
    {
        if (track.TryResetForRetry()) Enqueue(track);
    }

    private void Enqueue(TrackRecord track)
    {
        lock (_gate)
        {
            if (!_enqueued.Add(track.VideoId)) return;
        }
        _queue.Writer.TryWrite(track);
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        var lastCheck = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.PollIntervalMinutes));
            // Monitoring off: sleep until "Check now" or a settings change.
            var wait = _settings.MonitoringEnabled ? lastCheck + interval - DateTimeOffset.Now : Timeout.InfiniteTimeSpan;
            if (wait == Timeout.InfiniteTimeSpan || wait > TimeSpan.Zero)
            {
                try { await _pollSignal.WaitAsync(wait, ct); }
                catch (OperationCanceledException) { return; }
                while (_pollSignal.Wait(0)) { } // collapse bursts of signals into one wake-up
            }

            var manual = _manualCheckRequested;
            _manualCheckRequested = false;
            var due = _settings.MonitoringEnabled && DateTimeOffset.Now - lastCheck >= interval;
            if (!manual && !due) continue;

            try
            {
                await UpdateYtDlpIfDueAsync(ct);
                await CheckPlaylistsAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            lastCheck = DateTimeOffset.Now;
        }
    }

    private async Task UpdateYtDlpIfDueAsync(CancellationToken ct)
    {
        var last = _settings.LastToolUpdateCheck;
        if (last is not null && DateTimeOffset.Now - last < YtDlpUpdateInterval) return;
        Report("Checking for yt-dlp updates…");
        await _tools.UpdateYtDlpAsync(ct);
        UpdateSettings(s => s.LastToolUpdateCheck = DateTimeOffset.Now);
    }

    private async Task CheckPlaylistsAsync(CancellationToken ct)
    {
        var playlists = _settings.Playlists;
        if (playlists.Count == 0) { Report("Add a playlist to get started"); return; }

        Report("Checking playlists…");
        var added = 0;
        foreach (var url in playlists)
        {
            try
            {
                var info = await _ytDlp.GetPlaylistAsync(url, ct);
                foreach (var entry in info.Entries)
                    if (TryAdd(entry, url)) added++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Error($"Could not read playlist {url}", ex);
            }
        }
        if (added > 0) AppLog.Info($"Found {added} new video(s)");
        Report($"Last checked {DateTime.Now:t}");
    }

    private bool TryAdd(VideoEntry entry, string playlistUrl)
    {
        TrackRecord track;
        lock (_gate)
        {
            if (_byId.ContainsKey(entry.Id)) return false;
            track = new TrackRecord
            {
                VideoId = entry.Id,
                OriginalTitle = entry.Title,
                Channel = entry.Channel,
                DurationSeconds = entry.DurationSeconds,
                PlaylistUrl = playlistUrl,
            };
            track.PropertyChanged += OnTrackChanged;
            _tracks.Add(track);
            _byId.Add(track.VideoId, track);
        }
        ScheduleSave();
        TrackAdded?.Invoke(track);
        Enqueue(track);
        return true;
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var track in _queue.Reader.ReadAllAsync(ct))
            {
                lock (_gate) _enqueued.Remove(track.VideoId);
                if (!track.TryClaim()) continue; // already handled or no longer queued
                var job = await RunAsync(track, () => _processor.ProcessAsync(track, _settings, ct), ct);
                if (job is not null) _recordings.Writer.TryWrite(job);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Spotify recordings play in real time through one app, so they run one at a time here.</summary>
    private async Task RecorderAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _recordings.Reader.ReadAllAsync(ct))
            {
                var retryAt = await RunAsync(job.Track, () => _processor.RecordAsync(job, () => _settings, ct), ct);
                if (retryAt is { } at) _ = RequeueAtAsync(job, at, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Puts a job back in the recording queue later; on exit the track restarts from Queued next launch.</summary>
    private async Task RequeueAtAsync(SpotifyJob job, DateTimeOffset at, CancellationToken ct)
    {
        try
        {
            var wait = at - DateTimeOffset.Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _recordings.Writer.TryWrite(job);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Runs one track's work; a failure marks only that track failed. Rethrows cancellation on shutdown.</summary>
    private static async Task<T?> RunAsync<T>(TrackRecord track, Func<Task<T>> work, CancellationToken ct)
    {
        try
        {
            return await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            track.Status = TrackStatus.Queued; // resume next launch
            throw;
        }
        catch (Exception ex)
        {
            track.Note = ex.Message;
            track.Status = TrackStatus.Failed;
            AppLog.Error($"Failed: {track.OriginalTitle}", ex);
            return default;
        }
    }

    private void ClearWorkDir()
    {
        foreach (var dir in Directory.EnumerateDirectories(_paths.WorkDir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void OnTrackChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    /// <summary>Saves at most <see cref="SaveDelay"/> after the first unsaved change, even under constant activity.</summary>
    private void ScheduleSave()
    {
        if (_disposed || Interlocked.Exchange(ref _savePending, 1) == 1) return;
        try { _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void SaveLibrary()
    {
        Interlocked.Exchange(ref _savePending, 0);
        try
        {
            lock (_gate) JsonFile.Save(_paths.LibraryFile, _tracks);
        }
        catch (Exception ex) // runs on a timer thread: an escaped exception would kill the process
        {
            AppLog.Error("Could not save library", ex);
        }
    }

    private void Report(string status) => StatusChanged?.Invoke(status);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        Task[] running;
        lock (_gate) running = [.. _running];
        try
        {
            await Task.WhenAll(running).WaitAsync(ShutdownWait);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is not TimeoutException) AppLog.Error("A background task failed during shutdown", ex);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _saveTimer.DisposeAsync();
            SaveLibrary();
            _http.Dispose();
            _pollSignal.Dispose();
            _cts.Dispose();
        }
    }
}
