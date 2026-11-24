using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Songify.Core.Models;

public enum TrackStatus
{
    Queued,
    Matching,
    Downloading,
    Encoding,
    /// <summary>Queued for a Spotify recording (one at a time, maybe waiting until the PC is idle).</summary>
    WaitingToRecord,
    Recording,
    Done,
    Failed,
}

public enum TrackSource
{
    None,
    /// <summary>Official album audio from YouTube Music ("Artist - Topic" tracks).</summary>
    YouTubeMusic,
    /// <summary>Audio taken from the YouTube video itself (may include music-video extras).</summary>
    YouTubeVideo,
    /// <summary>Recorded from the Spotify desktop app while it played the song.</summary>
    Spotify,
}

/// <summary>
/// One playlist video and what Songify did with it. Mutated by worker threads;
/// raises PropertyChanged so the UI and the library store can react.
/// </summary>
public sealed class TrackRecord : INotifyPropertyChanged
{
    public required string VideoId { get; init; }
    public required string OriginalTitle { get; init; }
    public string? Channel { get; init; }
    public int? DurationSeconds { get; init; }
    public string? PlaylistUrl { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.Now;

    private TrackStatus _status;
    private TrackSource _source;
    private string? _artist, _title, _album, _matchedVideoId, _note, _filePath, _quality;
    private double? _matchScore;
    private bool _needsReview;

    public TrackStatus Status { get => _status; set => Set(ref _status, value); }
    public TrackSource Source { get => _source; set => Set(ref _source, value); }
    public string? Artist { get => _artist; set => Set(ref _artist, value); }
    public string? Title { get => _title; set => Set(ref _title, value); }
    public string? Album { get => _album; set => Set(ref _album, value); }
    /// <summary>ID of what was saved: a YouTube video ID, or the Spotify track ID for <see cref="TrackSource.Spotify"/>.</summary>
    public string? MatchedVideoId { get => _matchedVideoId; set => Set(ref _matchedVideoId, value); }
    public double? MatchScore { get => _matchScore; set => Set(ref _matchScore, value); }

    /// <summary>True when the result should be checked by the user (weak match or video fallback).</summary>
    public bool NeedsReview { get => _needsReview; set => Set(ref _needsReview, value); }

    public string? Note { get => _note; set => Set(ref _note, value); }
    public string? FilePath { get => _filePath; set => Set(ref _filePath, value); }

    /// <summary>Codec and bitrate of the saved file, e.g. "Opus 129 kbps (original)" or "MP3 320 kbps".</summary>
    public string? Quality { get => _quality; set => Set(ref _quality, value); }

    [JsonIgnore]
    public bool IsInProgress => Status is TrackStatus.Matching or TrackStatus.Downloading or TrackStatus.Encoding
        or TrackStatus.WaitingToRecord or TrackStatus.Recording;

    /// <summary>Waiting or being worked on.</summary>
    [JsonIgnore]
    public bool IsPending => Status == TrackStatus.Queued || IsInProgress;

    /// <summary>The matched song title once known, else the YouTube video title.</summary>
    [JsonIgnore]
    public string DisplayTitle => Title ?? OriginalTitle;

    private readonly object _claimLock = new();

    /// <summary>Atomically moves Queued → Matching. Only the worker that gets true may process the track.</summary>
    public bool TryClaim()
    {
        lock (_claimLock)
        {
            if (Status != TrackStatus.Queued) return false;
            Status = TrackStatus.Matching;
            return true;
        }
    }

    /// <summary>Atomically clears a failed track's results and re-queues it. False if it isn't Failed.</summary>
    public bool TryResetForRetry()
    {
        lock (_claimLock)
        {
            if (Status != TrackStatus.Failed) return false;
            Source = TrackSource.None;
            Artist = Title = Album = MatchedVideoId = Note = FilePath = Quality = null;
            MatchScore = null;
            NeedsReview = false;
            Status = TrackStatus.Queued;
            return true;
        }
    }

    /// <summary>The user has checked the saved song: it leaves "To check", and the note on why it was flagged goes.</summary>
    public void MarkChecked()
    {
        NeedsReview = false;
        Note = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Status))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInProgress)));
        if (name == nameof(Title))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayTitle)));
    }
}
