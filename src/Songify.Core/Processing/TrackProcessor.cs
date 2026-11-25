using Songify.Core.Audio;
using Songify.Core.Infrastructure;
using Songify.Core.Matching;
using Songify.Core.Models;
using Songify.Core.Spotify;
using Songify.Core.YouTube;

namespace Songify.Core.Processing;

/// <summary>A track waiting for the Spotify recorder, with what to save if recording fails.</summary>
public sealed class SpotifyJob
{
    internal SpotifyJob(TrackRecord track, TrackProcessor.SourcePlan plan, SpotifyMatch match, ParsedTitle parsed, string fallbackNote)
    {
        Track = track;
        Plan = plan;
        Match = match;
        Parsed = parsed;
        FallbackNote = fallbackNote;
    }

    public TrackRecord Track { get; }
    public SpotifyMatch Match { get; }
    internal TrackProcessor.SourcePlan Plan { get; }
    internal ParsedTitle Parsed { get; }
    /// <summary>Why YouTube Music had no album version; shown if the video's audio is saved instead.</summary>
    internal string FallbackNote { get; }
    /// <summary>Times Spotify didn't play the song so far.</summary>
    internal int DidNotPlayCount { get; set; }
}

/// <summary>
/// Turns one playlist video into an audio file:
/// 1) find the official album track on YouTube Music, 2) optionally record it from the Spotify app,
/// 3) otherwise use the video's own audio; then download, encode to the chosen format, tag, and save.
/// </summary>
public sealed class TrackProcessor(
    YtDlpClient ytDlp,
    YouTubeMusicClient youTubeMusic,
    SpotifyTrackResolver spotifyResolver,
    ISpotifyRecorder? spotifyRecorder,
    AudioEncoder encoder,
    HttpClient http,
    string workRoot)
{
    /// <summary>Matches at or above this are trusted; below it (but above the minimum) get flagged for review.</summary>
    private const double ConfidentScore = 0.85;

    /// <summary>Spotify Free has an unpublished daily limit on picking songs; wait it out a few times before giving up.</summary>
    internal const int DidNotPlayAttempts = 3;
    internal static readonly TimeSpan DidNotPlayRetryDelay = TimeSpan.FromHours(1);

    private static readonly object OutputLock = new();

    /// <summary>
    /// Processes a track the caller has already claimed (Status = Matching). Returns a job for
    /// <see cref="RecordAsync"/> when the song should be recorded from Spotify; otherwise the track is finished.
    /// </summary>
    public async Task<SpotifyJob?> ProcessAsync(TrackRecord track, AppSettings settings, CancellationToken ct)
    {
        var choice = await ChooseSourceAsync(track, settings, ct);
        if (choice is SpotifyJob job)
        {
            track.Status = TrackStatus.WaitingToRecord;
            return job;
        }
        if (choice is SourcePlan plan)
            await InWorkDirAsync(track.VideoId, work => DownloadAndSaveAsync(track, plan, work, settings, ct));
        return null;
    }

    /// <summary>
    /// Records a <see cref="SpotifyJob"/> and saves it. Run one at a time: it plays the song in real time.
    /// Falls back to the video's audio if Spotify can't play or record the song. Returns when to try again
    /// if Spotify played something else (e.g. the Free daily limit); the caller re-queues the job then, so one
    /// such song doesn't hold up the others. <paramref name="settings"/> is read live: a job may wait for days.
    /// While <paramref name="held"/> is true (an update waits to restart), a recording doesn't start.
    /// </summary>
    public async Task<DateTimeOffset?> RecordAsync(
        SpotifyJob job, Func<AppSettings> settings, CancellationToken ct, Func<bool>? held = null)
    {
        var track = job.Track;
        if (!settings().RecordFromSpotify || spotifyRecorder is null)
        {
            // Turned off while the job was waiting.
            await SaveVideoFallbackAsync(job, job.FallbackNote, settings(), ct);
            return null;
        }

        DateTimeOffset? retryAt = null;
        await InWorkDirAsync("spotify-" + track.VideoId, async work =>
        {
            var options = new RecordingOptions(
                () => settings() is { SpotifyOnlyWhenIdle: true } s ? TimeSpan.FromMinutes(s.SpotifyIdleMinutes) : null,
                () => settings().RecordFromSpotify,
                held ?? (() => false));
            void OnPhase(RecordingPhase phase)
            {
                track.Status = phase == RecordingPhase.Recording ? TrackStatus.Recording : TrackStatus.WaitingToRecord;
                track.Note = phase == RecordingPhase.WaitingForIdle
                    ? $"Records once this PC has been idle for {settings().SpotifyIdleMinutes} min"
                    : null;
            }

            string recording;
            try
            {
                recording = await spotifyRecorder.RecordAsync(job.Match.Track, options, work, OnPhase, ct);
            }
            catch (SpotifyRecordingException ex) when (ex.Failure == SpotifyFailure.DidNotPlay && ++job.DidNotPlayCount < DidNotPlayAttempts)
            {
                retryAt = DateTimeOffset.Now + DidNotPlayRetryDelay;
                track.Status = TrackStatus.WaitingToRecord;
                track.Note = $"{ex.Message} Trying again at {retryAt:t}.";
                AppLog.Warn($"{track.OriginalTitle}: {track.Note}");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Any recorder failure (no sound device, capture error…) still leaves the video's audio.
                var reason = ex is SpotifyRecordingException ? ex.Message : $"unexpected error ({ex.Message})";
                AppLog.Warn($"Spotify recording failed for \"{track.OriginalTitle}\": {reason}");
                await SaveVideoFallbackAsync(job, $"{job.FallbackNote}. Spotify recording failed: {reason.TrimEnd('.')}", settings(), ct);
                return;
            }

            var current = settings();
            var output = AudioEncoder.PlanRecordingOutput(current.Format, current.BitrateKbps);
            await SaveAsync(track, job.Plan, recording, output, job.Plan.Tags, work, current, ct);
        });
        return retryAt;
    }

    /// <summary>Reads the tags of a saved song's file.</summary>
    public Task<FileTags> ReadSavedTagsAsync(TrackRecord track, CancellationToken ct) =>
        encoder.ReadTagsAsync(SavedFile(track), ct);

    /// <summary>The file name (without extension) a saved song gets from the template; the edit dialog previews it.</summary>
    public static string FileBaseName(TrackRecord track, TrackTags tags, AppSettings settings) =>
        FileNamer.BuildBaseName(settings.FileNameTemplate, tags, track.MatchedVideoId ?? track.VideoId);

    /// <summary>Video IDs being edited: a second save of the same song would race the first one's file moves.</summary>
    private readonly HashSet<string> _editing = [];

    /// <summary>A song's tags are being saved (an update must not restart Songify now).</summary>
    public bool IsEditing
    {
        get { lock (_editing) return _editing.Count > 0; }
    }

    /// <summary>
    /// Writes new tags into a saved song's file (the audio is copied, not re-encoded; other tags and the cover are
    /// kept), renames it from the file name template, and marks the song checked. If the file can't be replaced
    /// (open in a player, read-only), nothing changes.
    /// </summary>
    public async Task EditAsync(TrackRecord track, TrackTags tags, AppSettings settings, CancellationToken ct)
    {
        var path = SavedFile(track);
        lock (_editing)
        {
            if (!_editing.Add(track.VideoId)) throw new InvalidOperationException("This song is already being saved.");
        }
        try
        {
            var extension = Path.GetExtension(path);
            var baseName = FileBaseName(track, tags, settings);
            await InWorkDirAsync("edit-" + track.VideoId, async work =>
            {
                var current = await encoder.ReadTagsAsync(path, ct);
                if (current.Tags != tags)
                {
                    var edited = Path.Combine(work, "edited" + extension);
                    await encoder.RewriteAsync(path, edited, AudioEncoder.PlanRetag(extension), current, tags, work, ct);
                    await Task.Run(() => FileSwap.Replace(edited, path), ct); // copying a big file must not freeze the window
                    AppLog.Info($"Edited the tags of {Path.GetFileName(path)}");
                }

                string? renameProblem = null;
                var finalPath = path;
                if (!FileNamer.HasBaseName(Path.GetFileName(path), baseName, extension))
                    (finalPath, renameProblem) = await Task.Run(() => Rename(path, baseName), ct);

                ApplyTags(track, tags);
                track.FilePath = finalPath;
                track.MarkChecked();
                if (renameProblem is not null) track.Note = renameProblem;
            });
        }
        finally
        {
            lock (_editing) _editing.Remove(track.VideoId);
        }
    }

    private static string SavedFile(TrackRecord track) =>
        track.Status == TrackStatus.Done && track.FilePath is { } path && File.Exists(path)
            ? path
            : throw new InvalidOperationException("The saved file isn't there anymore. It may have been moved, renamed or deleted.");

    /// <summary>Renames a saved file to the template's name. Returns the path it ends up at, and why not if it couldn't.</summary>
    private static (string Path, string? Problem) Rename(string path, string baseName)
    {
        var folder = Path.GetDirectoryName(path)!;
        lock (OutputLock)
        {
            var target = FileNamer.UniquePath(folder, baseName, Path.GetExtension(path), ownPath: path);
            try
            {
                File.Move(path, target);
                return (target, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn($"Couldn't rename {Path.GetFileName(path)}: {ex.Message}");
                return (path, $"Saved the new tags but couldn't rename the file: {ex.Message}");
            }
        }
    }

    private static void ApplyTags(TrackRecord track, TrackTags tags)
    {
        track.Artist = tags.Artist;
        track.Title = tags.Title;
        track.Album = tags.Album;
    }

    private async Task SaveVideoFallbackAsync(SpotifyJob job, string note, AppSettings settings, CancellationToken ct)
    {
        if (VideoPlan(job.Track, job.Parsed, note, settings) is { } plan)
            await InWorkDirAsync(job.Track.VideoId, work => DownloadAndSaveAsync(job.Track, plan, work, settings, ct));
    }

    /// <summary>Runs <paramref name="action"/> in a fresh temp folder that is deleted afterwards.</summary>
    private Task InWorkDirAsync(string name, Func<string, Task> action) => WorkDir.RunAsync(workRoot, name, action);

    private async Task DownloadAndSaveAsync(TrackRecord track, SourcePlan plan, string work, AppSettings settings, CancellationToken ct)
    {
        track.Status = TrackStatus.Downloading;
        var audio = await ytDlp.DownloadAudioAsync(plan.Id, plan.Source == TrackSource.YouTubeMusic, work, ct);
        var output = AudioEncoder.PlanOutput(settings.Format, settings.BitrateKbps, audio.Codec, audio.BitrateKbps);
        await SaveAsync(track, plan, audio.Path, output, WithDownloadedTags(plan.Tags, plan.Source, audio), work, settings, ct);
    }

    private async Task SaveAsync(
        TrackRecord track, SourcePlan plan, string input, OutputPlan output, TrackTags tags, string work,
        AppSettings settings, CancellationToken ct)
    {
        var cover = output.Cover != CoverEmbedding.None ? await TryDownloadCoverAsync(plan.CoverUrls, work, ct) : null;

        track.Status = TrackStatus.Encoding;
        var extension = output.Extension;
        var encoded = Path.Combine(work, "encoded" + extension);
        await encoder.EncodeAsync(input, encoded, output, tags, cover, work, ct);

        Directory.CreateDirectory(settings.OutputFolder);
        var baseName = FileNamer.BuildBaseName(settings.FileNameTemplate, tags, plan.Id);
        string finalPath;
        lock (OutputLock) // two workers may produce the same name at once
        {
            finalPath = FileNamer.UniquePath(settings.OutputFolder, baseName, extension);
            File.Move(encoded, finalPath);
        }

        track.Source = plan.Source;
        track.MatchedVideoId = plan.Id;
        ApplyTags(track, tags);
        track.MatchScore = plan.Score;
        track.NeedsReview = plan.NeedsReview;
        track.Note = plan.Note;
        track.FilePath = finalPath;
        track.Quality = output.Quality;
        track.Status = TrackStatus.Done;
        AppLog.Info($"Saved {Path.GetFileName(finalPath)} ({plan.Source})");
    }

    /// <summary>
    /// For album tracks, the official "Provided to YouTube" description names the real album (search
    /// results show a single's track title there) and the year. Video fallbacks keep what we parsed:
    /// their album/year may describe a different recording (Content ID) or a premiere date.
    /// </summary>
    public static TrackTags WithDownloadedTags(TrackTags planned, TrackSource source, DownloadedAudio audio) =>
        source == TrackSource.YouTubeMusic
            ? planned with { Album = audio.Album ?? planned.Album, Year = audio.ReleaseYear ?? planned.Year }
            : planned;

    /// <param name="Id">YouTube video ID to download, or the Spotify track ID for a recording.</param>
    internal sealed record SourcePlan(
        TrackSource Source, string Id, TrackTags Tags, IReadOnlyList<string> CoverUrls,
        double? Score, bool NeedsReview, string? Note);

    /// <summary>A <see cref="SourcePlan"/>, a <see cref="SpotifyJob"/>, or null when the track failed.</summary>
    private async Task<object?> ChooseSourceAsync(TrackRecord track, AppSettings settings, CancellationToken ct)
    {
        var parsed = TitleParser.Parse(track.OriginalTitle, track.Channel);

        IReadOnlyList<MusicTrack> candidates;
        try
        {
            candidates = await youTubeMusic.SearchSongsAsync(parsed.Query, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A temporary outage must not turn the whole queue into video-audio fallbacks.
            throw new InvalidOperationException($"YouTube Music search failed ({ex.Message}). Try again later.", ex);
        }

        // The playlist item is itself an album track (an "Artist - Topic" upload).
        var same = candidates.FirstOrDefault(c => c.VideoId == track.VideoId);
        if (same is not null)
            return MusicPlan(same, 1.0, needsReview: false, note: null);

        var best = TrackMatcher.Best(parsed, track.DurationSeconds, candidates);
        if (best is not null && best.Score >= settings.MinMatchScore)
        {
            var confident = best.Score >= ConfidentScore;
            AppLog.Info($"Matched \"{track.OriginalTitle}\" → {best.Track.Artists} - {best.Track.Title} ({best.Score:0.00})");
            return MusicPlan(best.Track, best.Score, !confident, confident ? null : "Lower-confidence match; check it's the right song");
        }

        if (track.Channel?.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Album audio even though search didn't return it.
            return new SourcePlan(TrackSource.YouTubeMusic, track.VideoId, VideoTags(track, parsed),
                YouTubeUrls.Thumbnails(track.VideoId), null, false, null);
        }

        var note = best is null
            ? "No YouTube Music results"
            : $"No confident YouTube Music match (best {best.Score:0.00}: {best.Track.Artists} - {best.Track.Title})";

        if (settings.RecordFromSpotify && spotifyRecorder is not null)
        {
            var match = await spotifyResolver.FindAsync(parsed, track.DurationSeconds, settings.MinMatchScore, ct);
            if (match is not null)
            {
                AppLog.Info($"Found \"{track.OriginalTitle}\" on Spotify → {match.Track.Artist} - {match.Track.Title} ({match.Score:0.00}); queued to record");
                return new SpotifyJob(track, SpotifyPlan(match), match, parsed, note);
            }
            note += ". Not found on Spotify";
        }

        return VideoPlan(track, parsed, note, settings);
    }

    /// <summary>The video's own audio, or null (track marked failed) when that fallback is turned off.</summary>
    private static SourcePlan? VideoPlan(TrackRecord track, ParsedTitle parsed, string note, AppSettings settings)
    {
        if (!settings.AllowVideoFallback)
        {
            track.Note = note + ". Saving the video's audio is turned off.";
            track.Status = TrackStatus.Failed;
            AppLog.Warn($"{track.OriginalTitle}: {track.Note}");
            return null;
        }

        AppLog.Info($"Using video audio for \"{track.OriginalTitle}\": {note}");
        // No score: the rejected candidate's score would read as a score for the saved video audio.
        return new SourcePlan(TrackSource.YouTubeVideo, track.VideoId, VideoTags(track, parsed), YouTubeUrls.Thumbnails(track.VideoId),
            null, true, note + ". Used the video's audio; it may include music-video extras.");
    }

    private static TrackTags VideoTags(TrackRecord track, ParsedTitle parsed) =>
        new(parsed.Title, parsed.Artist, null, parsed.Artist, YouTubeUrls.Video(track.VideoId));

    private static SourcePlan MusicPlan(MusicTrack match, double score, bool needsReview, string? note)
    {
        var tags = new TrackTags(match.Title, match.Artists, match.Album, match.PrimaryArtist, YouTubeUrls.Music(match.VideoId));
        var covers = new List<string>();
        if (match.ThumbnailUrl is not null) covers.Add(match.ThumbnailUrl);
        covers.AddRange(YouTubeUrls.Thumbnails(match.VideoId));
        return new SourcePlan(TrackSource.YouTubeMusic, match.VideoId, tags, covers, score, needsReview, note);
    }

    private static SourcePlan SpotifyPlan(SpotifyMatch match)
    {
        var t = match.Track;
        var tags = new TrackTags(t.Title, t.Artist, t.Album, t.Artist, SpotifyTrack.Url(t.Id), t.Year);
        var confident = match.Score >= ConfidentScore;
        return new SourcePlan(TrackSource.Spotify, t.Id, tags, t.CoverUrl is null ? [] : [t.CoverUrl], match.Score,
            !confident, confident ? null : "Lower-confidence Spotify match; check it's the right song");
    }

    /// <summary>Cover art is nice-to-have: any failure just means no cover.</summary>
    private async Task<string?> TryDownloadCoverAsync(IReadOnlyList<string> urls, string work, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) continue;
                var path = Path.Combine(work, "cover.jpg");
                await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync(ct), ct);
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
            }
        }
        return null;
    }
}
