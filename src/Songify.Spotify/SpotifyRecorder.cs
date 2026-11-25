using System.ComponentModel;
using System.Diagnostics;
using Songify.Core.Infrastructure;
using Songify.Core.Spotify;
using Windows.Foundation;
using Windows.Media.Control;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Songify.Spotify;

/// <summary>
/// Records songs from the Spotify desktop app: opens <c>spotify:track:ID</c> (which plays it from the
/// start, even on Free), captures only Spotify's audio, and uses the Windows media session (what Spotify
/// reports as playing) to tell the song from ads and whatever plays next. The PC's output is muted
/// while recording; Spotify's own volume must stay at 100% because the capture follows it.
/// </summary>
public sealed class SpotifyRecorder : ISpotifyRecorder
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StartSettle = TimeSpan.FromSeconds(8);
    /// <summary>A hung Spotify must not keep the PC muted: media-session calls give up after this.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    /// <summary>How long to wait for the song: Free plays up to about a minute of ads first.</summary>
    private static readonly TimeSpan PlayTimeout = TimeSpan.FromMinutes(2);
    /// <summary>Stop this long after the song should have ended, if Spotify doesn't report anything new.</summary>
    private static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(5);
    private const double LengthToleranceSeconds = 3;
    /// <summary>Input from remote-control tools would otherwise interrupt every attempt forever.</summary>
    private const int MaxInterruptions = 5;

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly MasterMute _mute;
    private GlobalSystemMediaTransportControlsSessionManager? _manager; // guarded by _oneAtATime

    /// <param name="stateDir">Where to keep the "output muted" marker (survives a crash).</param>
    public SpotifyRecorder(string stateDir) => _mute = new MasterMute(Path.Combine(stateDir, "muted-by-songify.txt"));

    public void RecoverFromCrash() => _mute.RestoreAfterCrash();

    public async Task<string> RecordAsync(
        SpotifyTrack track, RecordingOptions options, string workDir, Action<RecordingPhase> phase, CancellationToken ct)
    {
        await _oneAtATime.WaitAsync(ct);
        try
        {
            for (var interruptions = 0; ; )
            {
                await WaitUntilIdleAsync(options, phase, ct);
                phase(RecordingPhase.Recording);
                var path = await RecordOnceAsync(track, abortOnInput: options.RequiredIdle() is not null, workDir, ct);
                if (path is not null) return path;
                if (++interruptions >= MaxInterruptions)
                    throw new SpotifyRecordingException(SpotifyFailure.DidNotPlay, "The recording kept getting interrupted by keyboard or mouse use.");
                AppLog.Info($"Stopped recording \"{track.Title}\" because the PC is in use; it will be recorded once it's idle again");
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private static async Task WaitUntilIdleAsync(RecordingOptions options, Action<RecordingPhase> phase, CancellationToken ct)
    {
        for (var first = true; ; first = false)
        {
            if (!options.StillWanted())
                throw new SpotifyRecordingException(SpotifyFailure.TurnedOff, "Recording from Spotify was turned off.");
            if (!options.Held() && (options.RequiredIdle() is not { } idle || UserActivity.IsAway(idle))) return;
            if (first) phase(RecordingPhase.WaitingForIdle);
            await Task.Delay(IdlePoll, ct);
        }
    }

    /// <summary>The trimmed recording's path, or null when the user came back and the recording was dropped.</summary>
    private async Task<string?> RecordOnceAsync(SpotifyTrack track, bool abortOnInput, string workDir, CancellationToken ct)
    {
        var spotifyPid = await EnsureSpotifyRunningAsync(ct);
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct).WaitAsync(CallTimeout, ct);
        var inputAtStart = UserActivity.LastInputTick();
        var capturePath = Path.Combine(workDir, "capture.wav");

        float[] envelope;
        int songChange = -1, nextChange = -1;
        double reportedSeconds = 0;
        using (var capture = await ProcessCapture.StartAsync(spotifyPid, capturePath))
        {
            // Stamp each "now playing" / play-pause change with the capture's clock. Spotify's session
            // outlives this recording, so the handlers are removed again below.
            var lastEvent = 0;
            Session? hooked = null;
            TypedEventHandler<Session, MediaPropertiesChangedEventArgs> onMedia = (_, _) => Volatile.Write(ref lastEvent, capture.Blocks);
            TypedEventHandler<Session, PlaybackInfoChangedEventArgs> onPlayback = (_, _) => Volatile.Write(ref lastEvent, capture.Blocks);
            Session? CurrentSession()
            {
                var session = _manager.GetSessions().FirstOrDefault(s =>
                    s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase));
                if (session is not null && !ReferenceEquals(session, hooked))
                {
                    Unhook();
                    session.MediaPropertiesChanged += onMedia;
                    session.PlaybackInfoChanged += onPlayback;
                    hooked = session;
                }
                return session;
            }
            void Unhook()
            {
                if (hooked is null) return;
                hooked.MediaPropertiesChanged -= onMedia;
                hooked.PlaybackInfoChanged -= onPlayback;
                hooked = null;
            }

            try
            {
                using (_mute.Mute())
                {
                    Process.Start(new ProcessStartInfo(track.Uri) { UseShellExecute = true })?.Dispose();
                    var opened = Stopwatch.StartNew();
                    Stopwatch? playing = null;
                    (string? Title, string? Artist) seen = default;

                    while (nextChange < 0)
                    {
                        await Task.Delay(Poll, ct);
                        if (abortOnInput && UserActivity.LastInputTick() != inputAtStart)
                        {
                            await TryPauseAsync(CurrentSession(), ct);
                            return null;
                        }

                        var session = CurrentSession();
                        var now = await ReadAsync(session, ct);
                        var ours = now.Playing && SpotifyTrackResolver.IsSameSong(track, now.Title, now.Artist);
                        if (playing is null)
                        {
                            if (ours)
                            {
                                songChange = RecentEvent(Volatile.Read(ref lastEvent), capture.Blocks);
                                playing = Stopwatch.StartNew();
                            }
                            else if (opened.Elapsed > PlayTimeout)
                            {
                                await TryPauseAsync(session, ct);
                                throw new SpotifyRecordingException(SpotifyFailure.DidNotPlay, seen.Title is null
                                    ? "Spotify didn't play the song. Check that Spotify is signed in."
                                    : $"Spotify played \"{seen.Title}\" by {seen.Artist} instead of the song (Free accounts have a daily limit on choosing songs).");
                            }
                            if (now.Title is not null) seen = (now.Title, now.Artist);
                            continue;
                        }

                        if (ours) reportedSeconds = Math.Max(reportedSeconds, now.EndSeconds);
                        var expected = reportedSeconds > 0 ? reportedSeconds : track.DurationSeconds ?? 0;
                        if (!ours) nextChange = RecentEvent(Volatile.Read(ref lastEvent), capture.Blocks); // the next track, an ad, or stopped
                        else if (expected > 0 && playing.Elapsed.TotalSeconds > expected + EndGrace.TotalSeconds)
                            nextChange = capture.Blocks;
                    }
                    await TryPauseAsync(CurrentSession(), ct); // don't let Spotify's autoplay keep going
                    envelope = capture.Stop();
                }
            }
            finally
            {
                Unhook();
            }
            if (capture.Failure is { } failure)
                throw new SpotifyRecordingException(SpotifyFailure.WrongLength, $"The recording failed: {failure}.");
        }

        var song = RecordingCutter.FindSong(envelope, songChange, Math.Max(nextChange, songChange));
        if (song is not { } cut)
            throw new SpotifyRecordingException(SpotifyFailure.WrongLength, "The recording was silent. Keep Spotify's volume at 100% and don't mute it.");

        var recorded = TimeSpan.FromMilliseconds((cut.End - cut.Start) * RecordingCutter.BlockMs);
        double? expectedLength = reportedSeconds > 0 ? reportedSeconds : track.DurationSeconds;
        if (expectedLength is double secs && Math.Abs(recorded.TotalSeconds - secs) > LengthToleranceSeconds)
            throw new SpotifyRecordingException(SpotifyFailure.WrongLength,
                $"Recorded {recorded:m\\:ss} but the song is {TimeSpan.FromSeconds(secs):m\\:ss} long.");

        var output = Path.Combine(workDir, "spotify.wav");
        ProcessCapture.Extract(capturePath, cut.Start, cut.End, output);
        File.Delete(capturePath);
        AppLog.Info($"Recorded \"{track.Title}\" from Spotify ({recorded:m\\:ss})");
        return output;
    }

    /// <summary>
    /// The block of the change the poll just noticed. Spotify's event normally arrives just before; if none
    /// did (the session appeared after it), assume the change happened one poll ago.
    /// </summary>
    private static int RecentEvent(int lastEvent, int now)
    {
        var maxAge = (int)(TimeSpan.FromSeconds(1).TotalMilliseconds / RecordingCutter.BlockMs);
        var onePoll = (int)(Poll.TotalMilliseconds / RecordingCutter.BlockMs);
        return lastEvent >= now - maxAge ? Math.Min(lastEvent, now) : Math.Max(0, now - onePoll);
    }

    private readonly record struct NowPlaying(string? Title, string? Artist, bool Playing, double EndSeconds);

    /// <summary>What Spotify reports; nothing when the session is gone (Spotify quit) or not answering.</summary>
    private static async Task<NowPlaying> ReadAsync(Session? session, CancellationToken ct)
    {
        if (session is null) return default;
        try
        {
            var media = await session.TryGetMediaPropertiesAsync().AsTask(ct).WaitAsync(CallTimeout, ct);
            var playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var end = session.GetTimelineProperties().EndTime.TotalSeconds;
            return new NowPlaying(Blank(media.Title), Blank(media.Artist), playing, end);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException)
        {
            return default;
        }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static async Task TryPauseAsync(Session? session, CancellationToken ct)
    {
        if (session is null) return;
        try { await session.TryPauseAsync().AsTask(ct).WaitAsync(CallTimeout, ct); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or TimeoutException) { }
    }

    /// <summary>The main Spotify process's ID (the first started; the rest are its helpers), starting the app if needed.</summary>
    private static async Task<int> EnsureSpotifyRunningAsync(CancellationToken ct)
    {
        if (FindSpotify() is { } running) return running;

        AppLog.Info("Starting Spotify to record");
        try
        {
            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            throw new SpotifyRecordingException(SpotifyFailure.Unavailable,
                "The Spotify app isn't installed. Install it from the Microsoft Store or spotify.com and sign in.");
        }

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < StartTimeout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            if (FindSpotify() is { } started)
            {
                await Task.Delay(StartSettle, ct); // signing in and loading
                return started;
            }
        }
        throw new SpotifyRecordingException(SpotifyFailure.Unavailable, "Spotify didn't start.");
    }

    private static int? FindSpotify()
    {
        var processes = Process.GetProcessesByName("Spotify");
        try
        {
            return processes
                .Select(p => (p.Id, Start: StartTime(p)))
                .Where(p => p.Start is not null)
                .OrderBy(p => p.Start)
                .Select(p => (int?)p.Id)
                .FirstOrDefault();
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static DateTime? StartTime(Process p)
    {
        try { return p.StartTime; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return null; } // exited
    }
}
