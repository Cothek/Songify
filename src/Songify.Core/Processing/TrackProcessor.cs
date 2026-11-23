using Songify.Core.Audio;
using Songify.Core.Infrastructure;
using Songify.Core.Matching;
using Songify.Core.Models;
using Songify.Core.YouTube;

namespace Songify.Core.Processing;

/// <summary>
/// Turns one playlist video into an audio file:
/// 1) find the official album track on YouTube Music, 2) otherwise use the video's own audio,
/// then download, encode to the chosen format, tag, and save.
/// </summary>
public sealed class TrackProcessor(
    YtDlpClient ytDlp,
    YouTubeMusicClient youTubeMusic,
    AudioEncoder encoder,
    HttpClient http,
    string workRoot)
{
    /// <summary>Matches at or above this are trusted; below it (but above the minimum) get flagged for review.</summary>
    private const double ConfidentScore = 0.85;

    private static readonly object OutputLock = new();

    /// <summary>Processes a track the caller has already claimed (Status = Matching).</summary>
    public async Task ProcessAsync(TrackRecord track, AppSettings settings, CancellationToken ct)
    {
        var work = Path.Combine(workRoot, track.VideoId);
        Directory.CreateDirectory(work);
        try
        {
            var plan = await ChooseSourceAsync(track, settings, ct);
            if (plan is null) return;

            track.Status = TrackStatus.Downloading;
            var audio = await ytDlp.DownloadAudioAsync(plan.VideoId, plan.Source == TrackSource.YouTubeMusic, work, ct);
            var output = AudioEncoder.PlanOutput(settings.Format, settings.BitrateKbps, audio.Codec, audio.BitrateKbps);
            var cover = output.Cover != CoverEmbedding.None ? await TryDownloadCoverAsync(plan.CoverUrls, work, ct) : null;
            var tags = WithDownloadedTags(plan.Tags, plan.Source, audio);

            track.Status = TrackStatus.Encoding;
            var extension = output.Extension;
            var encoded = Path.Combine(work, "encoded" + extension);
            await encoder.EncodeAsync(audio.Path, encoded, output, tags, cover, work, ct);

            Directory.CreateDirectory(settings.OutputFolder);
            var baseName = FileNamer.BuildBaseName(settings.FileNameTemplate, tags, plan.VideoId);
            string finalPath;
            lock (OutputLock) // two workers may produce the same name at once
            {
                finalPath = FileNamer.UniquePath(settings.OutputFolder, baseName, extension);
                File.Move(encoded, finalPath);
            }

            track.Source = plan.Source;
            track.MatchedVideoId = plan.VideoId;
            track.Artist = tags.Artist;
            track.Title = tags.Title;
            track.Album = tags.Album;
            track.MatchScore = plan.Score;
            track.NeedsReview = plan.NeedsReview;
            track.Note = plan.Note;
            track.FilePath = finalPath;
            track.Quality = output.Quality;
            track.Status = TrackStatus.Done;
            AppLog.Info($"Saved {Path.GetFileName(finalPath)} ({plan.Source})");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
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

    private sealed record SourcePlan(
        TrackSource Source, string VideoId, TrackTags Tags, IReadOnlyList<string> CoverUrls,
        double? Score, bool NeedsReview, string? Note);

    private async Task<SourcePlan?> ChooseSourceAsync(TrackRecord track, AppSettings settings, CancellationToken ct)
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

        var note = best is null
            ? "No YouTube Music results"
            : $"No confident YouTube Music match (best {best.Score:0.00}: {best.Track.Artists} - {best.Track.Title})";
        var videoTags = new TrackTags(parsed.Title, parsed.Artist, null, parsed.Artist, YouTubeUrls.Video(track.VideoId));
        var thumbnails = YouTubeUrls.Thumbnails(track.VideoId);

        if (track.Channel?.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Album audio even though search didn't return it.
            return new SourcePlan(TrackSource.YouTubeMusic, track.VideoId, videoTags, thumbnails, null, false, null);
        }

        if (!settings.AllowVideoFallback)
        {
            track.Note = note + ". Saving the video's audio is turned off.";
            track.Status = TrackStatus.Failed;
            AppLog.Warn($"{track.OriginalTitle}: {track.Note}");
            return null;
        }

        AppLog.Info($"Using video audio for \"{track.OriginalTitle}\": {note}");
        // No score: the rejected candidate's score would read as a score for the saved video audio.
        return new SourcePlan(TrackSource.YouTubeVideo, track.VideoId, videoTags, thumbnails, null, true,
            note + ". Used the video's audio; it may include music-video extras.");
    }

    private static SourcePlan MusicPlan(MusicTrack match, double score, bool needsReview, string? note)
    {
        var tags = new TrackTags(match.Title, match.Artists, match.Album, match.PrimaryArtist, YouTubeUrls.Music(match.VideoId));
        var covers = new List<string>();
        if (match.ThumbnailUrl is not null) covers.Add(match.ThumbnailUrl);
        covers.AddRange(YouTubeUrls.Thumbnails(match.VideoId));
        return new SourcePlan(TrackSource.YouTubeMusic, match.VideoId, tags, covers, score, needsReview, note);
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
