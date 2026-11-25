namespace Songify.Core.Spotify;

/// <summary>A Spotify track found for a playlist video. Tags come from Deezer's catalog (same ISRC).</summary>
public sealed record SpotifyTrack(
    string Id, string Title, string Artist, string? Album, int? Year, int? DurationSeconds, string? CoverUrl)
{
    public string Uri => $"spotify:track:{Id}";
    public static string Url(string id) => $"https://open.spotify.com/track/{id}";
}

public enum RecordingPhase
{
    /// <summary>Waiting until nobody is using the PC (see <see cref="RecordingOptions.RequiredIdle"/>).</summary>
    WaitingForIdle,
    Recording,
}

/// <summary>Read while the recorder waits, so settings changed in the meantime apply.</summary>
/// <param name="RequiredIdle">
/// Record only after this long without keyboard/mouse input and with no other app playing sound;
/// a recording the user interrupts is discarded and retried. Null: record right away.
/// </param>
/// <param name="StillWanted">False once the user turned Spotify recording off: the recorder gives up (<see cref="SpotifyFailure.TurnedOff"/>).</param>
/// <param name="Held">True while an update waits to restart Songify: don't start recording (keep waiting).</param>
public sealed record RecordingOptions(Func<TimeSpan?> RequiredIdle, Func<bool> StillWanted, Func<bool> Held);

public enum SpotifyFailure
{
    /// <summary>The Spotify app isn't installed or didn't start.</summary>
    Unavailable,
    /// <summary>Spotify played something else or nothing (e.g. the Free daily limit). Worth retrying later.</summary>
    DidNotPlay,
    /// <summary>The recording is unusable: wrong length (skipped, stalled), silent, or glitched.</summary>
    WrongLength,
    /// <summary>Spotify recording was turned off while the song waited to be recorded.</summary>
    TurnedOff,
}

public sealed class SpotifyRecordingException(SpotifyFailure failure, string message) : Exception(message)
{
    public SpotifyFailure Failure { get; } = failure;
}

/// <summary>Plays a track in the Spotify desktop app and records it. Implemented in Songify.Spotify.</summary>
public interface ISpotifyRecorder
{
    /// <summary>Undoes what a recording cut short by a crash left behind (the muted sound output). Call once at startup.</summary>
    void RecoverFromCrash();

    /// <summary>
    /// Records <paramref name="track"/> into a WAV file in <paramref name="workDir"/> and returns its path,
    /// trimmed to the song. Recordings run one at a time. Throws <see cref="SpotifyRecordingException"/>.
    /// </summary>
    Task<string> RecordAsync(
        SpotifyTrack track, RecordingOptions options, string workDir, Action<RecordingPhase> phase, CancellationToken ct);
}
