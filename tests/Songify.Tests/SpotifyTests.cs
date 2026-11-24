using Songify.Core.Audio;
using Songify.Core.Matching;
using Songify.Core.Models;
using Songify.Core.Processing;
using Songify.Core.Spotify;
using Songify.Core.Tools;
using Songify.Core.YouTube;

namespace Songify.Tests;

public class RecordingCutterTests
{
    private const float Loud = -15f;
    private const float Silent = float.NegativeInfinity;

    /// <summary>Envelope from (seconds, loud?) segments, 100 blocks per second.</summary>
    private static List<float> Envelope(params (double Seconds, bool Loud)[] parts) =>
        parts.SelectMany(p => Enumerable.Repeat(p.Loud ? Loud : Silent, (int)Math.Round(p.Seconds * 100))).ToList();

    [Fact]
    public void Song_after_leading_silence_is_cut_at_its_first_sound_and_last_sound()
    {
        // 5 s of nothing while Spotify starts, the 30 s song, then 1 s of silence before the next change.
        var env = Envelope((5, false), (30, true), (1, false));
        var song = RecordingCutter.FindSong(env, songChange: 480, nextChange: env.Count);
        Assert.Equal((500, 3500), song);
    }

    [Fact]
    public void An_ads_tail_before_the_change_is_skipped()
    {
        // Ad audio runs until 0.3 s before Spotify reports the song; 0.5 s gap; song.
        var env = Envelope((10, true), (0.5, false), (20, true));
        var song = RecordingCutter.FindSong(env, songChange: 1030, nextChange: env.Count);
        Assert.Equal((1050, 3050), song);
    }

    [Fact]
    public void A_quiet_moment_inside_the_song_does_not_move_the_start()
    {
        // The song starts with a short note, pauses 0.3 s, then continues.
        var env = Envelope((5, false), (0.5, true), (0.3, false), (20, true));
        var song = RecordingCutter.FindSong(env, songChange: 480, nextChange: env.Count);
        Assert.Equal(500, song!.Value.Start);
    }

    [Fact]
    public void Without_any_gap_the_song_starts_at_the_change()
    {
        var env = Envelope((30, true));
        var song = RecordingCutter.FindSong(env, songChange: 1000, nextChange: 2500);
        Assert.Equal((1000, 2500), song);
    }

    [Fact]
    public void Sound_after_the_next_change_is_not_part_of_the_song()
    {
        var env = Envelope((1, false), (10, true), (0.4, false), (5, true));
        var song = RecordingCutter.FindSong(env, songChange: 90, nextChange: 1130);
        Assert.Equal((100, 1100), song);
    }

    [Fact]
    public void A_late_change_still_finds_the_real_start_not_a_pause_inside_the_song()
    {
        // The song starts at 5 s, Spotify reports it 1 s later, and the song pauses 0.3 s at 7 s.
        var env = Envelope((5, false), (2, true), (0.3, false), (20, true));
        var song = RecordingCutter.FindSong(env, songChange: 600, nextChange: env.Count);
        Assert.Equal(500, song!.Value.Start);
    }

    [Fact]
    public void An_ad_running_straight_into_the_song_cuts_at_the_change_not_at_a_later_pause()
    {
        // Ad until 10 s with no gap, song from 10 s with a 0.3 s pause 1.5 s in.
        var env = Envelope((10, true), (1.5, true), (0.3, false), (20, true));
        var song = RecordingCutter.FindSong(env, songChange: 1000, nextChange: env.Count);
        Assert.Equal(1000, song!.Value.Start);
    }

    [Fact]
    public void A_silent_capture_has_no_song()
    {
        var env = Envelope((10, false));
        Assert.Null(RecordingCutter.FindSong(env, songChange: 100, nextChange: env.Count));
    }

    [Fact]
    public void Loudness_of_full_scale_and_silence()
    {
        Assert.Equal(0, RecordingCutter.Db(4, 4), 3);
        Assert.Equal(float.NegativeInfinity, RecordingCutter.Db(0, 100));
        Assert.True(RecordingCutter.IsSilent(RecordingCutter.Db(1e-8 * 100, 100))); // -80 dB
    }
}

public class SpotifyResolverTests
{
    private static readonly SpotifyTrack GetLucky =
        new("118Opfz7qv2KA1g3Pmyk9T", "Get Lucky (Radio Edit - feat. Pharrell Williams and Nile Rodgers)", "Daft Punk",
            "Get Lucky", 2013, 248, null);

    [Fact]
    public void Parses_deezer_search_results()
    {
        const string json = """
            {"data":[
              {"id":66609426,"title":"Get Lucky (Radio Edit)","isrc":"USQX91300809","duration":248,
               "artist":{"name":"Daft Punk"},"album":{"id":6575789,"title":"Get Lucky","cover_xl":"https://e/c.jpg"}},
              {"id":1,"title":"","artist":{"name":"Nobody"}},
              {"id":2,"title":"No ISRC","artist":{"name":"Someone"},"album":{}}
            ],"total":3}
            """;
        var tracks = SpotifyTrackResolver.ParseDeezerSearch(json);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(new SpotifyTrackResolver.DeezerTrack("Get Lucky (Radio Edit)", "Daft Punk", "Get Lucky", 6575789, 248,
            "USQX91300809", "https://e/c.jpg"), tracks[0]);
        Assert.Null(tracks[1].Isrc);
        Assert.Empty(SpotifyTrackResolver.ParseDeezerSearch("""{"error":{"type":"Exception","code":4}}"""));
    }

    [Fact]
    public void Parses_spotify_ids_from_musicbrainz_relations()
    {
        const string json = """
            {"isrc":"USUG11904206","recordings":[
              {"relations":[{"url":{"resource":"https://open.spotify.com/track/0sf12qNH5qcw8qpgymFOqD"}},
                            {"url":{"resource":"https://www.deezer.com/track/1"}}]},
              {"relations":[{"url":{"resource":"https://open.spotify.com/track/0sf12qNH5qcw8qpgymFOqD"}},
                            {"url":{"resource":"https://open.spotify.com/track/6qYkmqFsXbj8CQjAdbYz07?si=x"}},
                            {"url":{"resource":"https://open.spotify.com/album/6qYkmqFsXbj8CQjAdbYz07"}}]},
              {}
            ]}
            """;
        Assert.Equal(["0sf12qNH5qcw8qpgymFOqD", "6qYkmqFsXbj8CQjAdbYz07"], SpotifyTrackResolver.ParseSpotifyIds(json));
        Assert.Empty(SpotifyTrackResolver.ParseSpotifyIds("""{"error":"Not Found"}"""));
    }

    [Theory]
    [InlineData("Get Lucky (feat. Pharrell Williams & Nile Rodgers) - Radio Edit", "Daft Punk, Pharrell Williams, Nile Rodgers", true)]
    [InlineData("Get Lucky", "Daft Punk", true)]
    [InlineData("Get Lucky", null, true)]
    [InlineData("Advertisement", "Spotify", false)]
    [InlineData("Get Lucky", "Some Cover Band", false)]
    [InlineData("Instant Crush", "Daft Punk", false)]
    [InlineData(null, "Daft Punk", false)]
    public void Recognizes_the_song_spotify_reports_as_playing(string? title, string? artist, bool expected) =>
        Assert.Equal(expected, SpotifyTrackResolver.IsSameSong(GetLucky, title, artist));
}

public class SpotifyOutputTests
{
    [Fact]
    public void Original_saves_recordings_as_vorbis_at_spotify_free_quality()
    {
        var plan = AudioEncoder.PlanRecordingOutput(AudioFormat.Original, 320);
        Assert.Equal(".ogg", plan.Extension);
        Assert.Equal(["-c:a", "libvorbis", "-b:a", "160k"], plan.AudioArgs);
        Assert.Equal(CoverEmbedding.VorbisComment, plan.Cover);
        Assert.Equal("Vorbis 160 kbps (Spotify's quality)", plan.Quality);
    }

    [Fact]
    public void Other_formats_encode_recordings_as_chosen()
    {
        var mp3 = AudioEncoder.PlanRecordingOutput(AudioFormat.Mp3, 256);
        Assert.Equal(".mp3", mp3.Extension);
        Assert.Equal(AudioEncoder.PlanOutput(AudioFormat.Mp3, 256, null, null).AudioArgs, mp3.AudioArgs);
        Assert.Equal("MP3 256 kbps", mp3.Quality);
        Assert.Equal(".flac", AudioEncoder.PlanRecordingOutput(AudioFormat.Flac, 256).Extension);
    }

    [Fact]
    public void Spotify_recording_is_off_by_default_and_idle_minutes_are_sanitized()
    {
        var defaults = new AppSettings();
        Assert.False(defaults.RecordFromSpotify);
        Assert.True(defaults.SpotifyOnlyWhenIdle);
        Assert.Equal(defaults.SpotifyIdleMinutes, new AppSettings { SpotifyIdleMinutes = 0 }.Sanitize().SpotifyIdleMinutes);
    }

    [Theory]
    [InlineData(TrackStatus.WaitingToRecord)]
    [InlineData(TrackStatus.Recording)]
    public void Recording_statuses_count_as_in_progress_so_a_restart_requeues_them(TrackStatus status) =>
        Assert.True(new TrackRecord { VideoId = "x", OriginalTitle = "x", Status = status }.IsInProgress);
}

/// <summary>TrackProcessor's Spotify paths with a fake recorder. Video fallback is off so nothing is downloaded.</summary>
public sealed class SpotifyRecordingFlowTests : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("songify-flow-");

    private sealed class FakeRecorder(Func<Exception?> next) : ISpotifyRecorder
    {
        public int Calls { get; private set; }
        public void RecoverFromCrash() { }
        public Task<string> RecordAsync(SpotifyTrack track, RecordingOptions options, string workDir,
            Action<RecordingPhase> phase, CancellationToken ct)
        {
            Calls++;
            phase(RecordingPhase.Recording);
            throw next() ?? new InvalidOperationException("no recording in tests");
        }
    }

    private TrackProcessor Processor(ISpotifyRecorder recorder)
    {
        var tools = new ToolManager(Path.Combine(_dir.FullName, "tools"), _http);
        return new TrackProcessor(new YtDlpClient(tools), new YouTubeMusicClient(_http), new SpotifyTrackResolver(_http),
            recorder, new AudioEncoder(tools), _http, _dir.FullName);
    }

    private static (SpotifyJob Job, TrackRecord Track) Job()
    {
        var track = new TrackRecord { VideoId = "abcdefghijk", OriginalTitle = "Daft Punk - Get Lucky", Status = TrackStatus.WaitingToRecord };
        var spotify = new SpotifyTrack("118Opfz7qv2KA1g3Pmyk9T", "Get Lucky", "Daft Punk", null, null, 248, null);
        var plan = new TrackProcessor.SourcePlan(TrackSource.Spotify, spotify.Id,
            new TrackTags("Get Lucky", "Daft Punk", null, "Daft Punk", null), [], 0.9, false, null);
        var job = new SpotifyJob(track, plan, new SpotifyMatch(spotify, 0.9), new ParsedTitle("Daft Punk", "Get Lucky", false),
            "No YouTube Music results");
        return (job, track);
    }

    private static readonly AppSettings Settings = new() { RecordFromSpotify = true, AllowVideoFallback = false };

    [Fact]
    public async Task Spotify_not_playing_the_song_is_retried_later_then_falls_back()
    {
        var recorder = new FakeRecorder(() => new SpotifyRecordingException(SpotifyFailure.DidNotPlay, "Spotify played \"Ad\" by Spotify instead."));
        var processor = Processor(recorder);
        var (job, track) = Job();

        for (var attempt = 1; attempt < TrackProcessor.DidNotPlayAttempts; attempt++)
        {
            var retryAt = await processor.RecordAsync(job, () => Settings, CancellationToken.None);
            Assert.NotNull(retryAt);
            Assert.InRange(retryAt.Value - DateTimeOffset.Now, TimeSpan.FromMinutes(59), TrackProcessor.DidNotPlayRetryDelay);
            Assert.Equal(TrackStatus.WaitingToRecord, track.Status);
            Assert.Contains("Trying again at", track.Note);
        }

        Assert.Null(await processor.RecordAsync(job, () => Settings, CancellationToken.None));
        Assert.Equal(TrackProcessor.DidNotPlayAttempts, recorder.Calls);
        Assert.Equal(TrackStatus.Failed, track.Status);
        Assert.Contains("Spotify recording failed: Spotify played \"Ad\" by Spotify instead. Saving the video's audio is turned off.", track.Note);
    }

    [Fact]
    public async Task Any_recorder_error_falls_back_to_the_video_audio_path()
    {
        var processor = Processor(new FakeRecorder(() => new System.Runtime.InteropServices.COMException("no sound device")));
        var (job, track) = Job();

        Assert.Null(await processor.RecordAsync(job, () => Settings, CancellationToken.None));
        Assert.Equal(TrackStatus.Failed, track.Status); // fallback ran; it's off in this test
        Assert.Contains("unexpected error (no sound device)", track.Note);
        Assert.DoesNotContain("..", track.Note);
    }

    [Fact]
    public async Task Turning_spotify_recording_off_skips_the_recorder()
    {
        var recorder = new FakeRecorder(() => null);
        var (job, track) = Job();

        await Processor(recorder).RecordAsync(job, () => new AppSettings { RecordFromSpotify = false, AllowVideoFallback = false }, CancellationToken.None);
        Assert.Equal(0, recorder.Calls);
        Assert.Equal(TrackStatus.Failed, track.Status);
        Assert.Equal("No YouTube Music results. Saving the video's audio is turned off.", track.Note);
    }

    public void Dispose()
    {
        _http.Dispose();
        _dir.Delete(recursive: true);
    }
}
