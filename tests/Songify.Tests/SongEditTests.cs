using Songify.Core.Audio;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Processing;
using Songify.Core.Spotify;
using Songify.Core.Tools;
using Songify.Core.YouTube;

namespace Songify.Tests;

public class SongEditTests
{
    [Fact]
    public void Probe_tags_merge_file_and_audio_stream_tags_case_insensitively()
    {
        // Ogg keeps tags on the audio stream (keys as written); the cover is a video stream.
        var ogg = AudioEncoder.ParseProbeTags("""
            {"streams": [
              {"codec_type": "audio", "tags": {"TITLE": "Get Lucky", "ARTIST": "Daft Punk", "album_artist": "Daft Punk",
                                               "DATE": "2013-05-17", "comment": "https://x"}},
              {"codec_type": "video", "tags": {"comment": "Cover (front)"}}
            ], "format": {}}
            """, "file name");
        Assert.Equal(new TrackTags("Get Lucky", "Daft Punk", null, "Daft Punk", "https://x", 2013), ogg.Tags);
        Assert.True(ogg.HasCover);
        Assert.Empty(ogg.Other);

        // MP3/M4A keep them on the file; file tags win over stream tags. Other tags are kept, technical ones dropped.
        var mp3 = AudioEncoder.ParseProbeTags("""
            {"streams": [{"codec_type": "audio", "tags": {"title": "stream title", "handler_name": "SoundHandler"}}],
             "format": {"tags": {"title": "Song", "album": "Album", "date": "not a year", "artist": " ",
                                 "genre": "Disco", "track": "8/13", "encoder": "Lavf61"}}}
            """, "file name");
        Assert.Equal(new TrackTags("Song", null, "Album", null, null), mp3.Tags);
        Assert.False(mp3.HasCover);
        Assert.Equal(new Dictionary<string, string> { ["genre"] = "Disco", ["track"] = "8/13" }, mp3.Other);

        Assert.Equal("file name", AudioEncoder.ParseProbeTags("""{"streams": [], "format": {}}""", "file name").Tags.Title);
    }

    [Theory]
    [InlineData(".opus", CoverEmbedding.VorbisComment)]
    [InlineData(".OGG", CoverEmbedding.VorbisComment)]
    [InlineData(".mp3", CoverEmbedding.AttachedPicture)]
    [InlineData(".m4a", CoverEmbedding.AttachedPicture)]
    [InlineData(".flac", CoverEmbedding.AttachedPicture)]
    [InlineData(".wav", CoverEmbedding.None)]
    [InlineData(".mka", CoverEmbedding.None)]
    public void Retag_copies_the_audio_into_the_same_container(string extension, CoverEmbedding cover)
    {
        var plan = AudioEncoder.PlanRetag(extension);

        Assert.Equal(extension.ToLowerInvariant(), plan.Extension);
        Assert.Equal(["-c:a", "copy"], plan.AudioArgs.Take(2));
        Assert.Equal(extension == ".mp3", plan.AudioArgs.Contains("-id3v2_version"));
        Assert.Equal(cover, plan.Cover);
    }

    [Fact]
    public void Cover_taken_from_a_saved_file_is_copied_not_recompressed()
    {
        var plan = AudioEncoder.PlanRetag(".mp3");
        var args = AudioEncoder.BuildArgs("in.mp3", "out.mp3", plan, new TrackTags("T", null, null, null, null), "cover.jpg", null,
            keptTags: new Dictionary<string, string> { ["genre"] = "Disco" });

        Assert.Equal("copy", args[args.IndexOf("-c:v") + 1]);
        Assert.DoesNotContain("-vf", args);
        Assert.Contains("attached_pic", args);
        Assert.Contains("genre=Disco", args);
    }

    [Fact]
    public void Kept_tags_go_into_the_ogg_metadata_file_escaped()
    {
        var text = AudioEncoder.BuildFfMetadata(new TrackTags("T", null, null, null, null), null,
            new Dictionary<string, string> { ["GENRE"] = "Disco; Funk", ["odd=key"] = "x" });

        Assert.Contains("GENRE=Disco\\; Funk\n", text);
        Assert.Contains("odd\\=key=x\n", text);
    }

    [Theory]
    [InlineData("2013", 2013)]
    [InlineData("2013-05-17", 2013)]
    [InlineData(" 1999/01 ", 1999)]
    [InlineData("20130517", null)]
    [InlineData("0000", null)]
    [InlineData("abcd", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Release_year_is_read_from_the_start_of_a_date(string? date, int? year) =>
        Assert.Equal(year, ReleaseYear.FromDate(date));

    [Fact]
    public void Numeric_release_years_must_be_plausible_whole_years()
    {
        Assert.Equal(2013, ReleaseYear.From(2013.0));
        Assert.Null(ReleaseYear.From(0));
        Assert.Null(ReleaseYear.From(2013.5));
        Assert.Null(ReleaseYear.From(null));
    }

    [Fact]
    public void Jpeg_size_is_read_from_the_frame_header()
    {
        byte[] jpeg =
        [
            0xFF, 0xD8,                                     // start of image
            0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00,             // APP0, 2 bytes of data
            0xFF, 0xC4, 0x00, 0x03, 0x00,                   // DHT: not a frame header
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0x20, 0x01, 0xE0, 0x03, // SOF0: 8 bit, height 544, width 480
            0, 0, 0, 0, 0, 0, 0, 0, 0,
        ];
        Assert.Equal((480, 544), AudioEncoder.JpegSize(jpeg));
        Assert.Null(AudioEncoder.JpegSize("\x89PNG\r\n\x1a\n0000000000"u8));
        Assert.Null(AudioEncoder.JpegSize([0xFF, 0xD8, 0xFF, 0xE0, 0x00]));
    }

    [Theory]
    [InlineData("Daft Punk - Get Lucky.opus", true)]
    [InlineData("Daft Punk - Get Lucky (2).opus", true)]
    [InlineData("Daft Punk - Get Lucky (12).OPUS", true)]
    [InlineData("daft punk - get lucky.opus", false)] // case-only change: still renamed
    [InlineData("Daft Punk - Get Lucky (x).opus", false)]
    [InlineData("Daft Punk - Get Lucky.mp3", false)]
    [InlineData("Daft Punk - Get Lucky (Radio Edit).opus", false)]
    public void File_keeps_its_name_when_it_already_matches(string fileName, bool keeps) =>
        Assert.Equal(keeps, FileNamer.HasBaseName(fileName, "Daft Punk - Get Lucky", ".opus"));

    [Fact]
    public void Marking_checked_clears_the_flag_and_its_note()
    {
        var track = new TrackRecord { VideoId = "v", OriginalTitle = "x", NeedsReview = true, Note = "Lower-confidence match" };
        track.MarkChecked();
        Assert.False(track.NeedsReview);
        Assert.Null(track.Note);
    }
}

/// <summary>Edits real files with the real ffmpeg (opt-in: SONGIFY_INTEGRATION=1, tools already downloaded).</summary>
public sealed class SongEditIntegrationTests : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("songify-edit-");
    private readonly ToolManager _tools;
    private readonly AudioEncoder _encoder;
    private readonly TrackProcessor _processor;
    private readonly AppSettings _settings;
    private static bool Enabled => Environment.GetEnvironmentVariable("SONGIFY_INTEGRATION") == "1";

    public SongEditIntegrationTests()
    {
        _tools = new ToolManager(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Songify", "tools"), _http);
        _encoder = new AudioEncoder(_tools);
        var work = Directory.CreateDirectory(Path.Combine(_dir.FullName, "work")).FullName;
        _processor = new TrackProcessor(new YtDlpClient(_tools), new YouTubeMusicClient(_http), new SpotifyTrackResolver(_http),
            null, _encoder, _http, work);
        _settings = new AppSettings { OutputFolder = Directory.CreateDirectory(Path.Combine(_dir.FullName, "out")).FullName };
    }

    public void Dispose()
    {
        _http.Dispose();
        _dir.Delete(recursive: true);
    }

    private static readonly TrackTags Before = new("Old Title", "Old Artist", null, "Old Artist", "https://www.youtube.com/watch?v=v", null);
    private static readonly TrackTags After = new("Get Lucky", "Daft Punk, Pharrell Williams", "Random Access Memories", "Daft Punk",
        "https://www.youtube.com/watch?v=v", 2013);

    private static readonly Dictionary<string, string> Genre = new() { ["genre"] = "Disco" };

    /// <summary>A saved song as Songify would leave it: encoded with tags and cover, flagged for review.</summary>
    private async Task<TrackRecord> SavedSongAsync(AudioFormat format)
    {
        var source = Path.Combine(_dir.FullName, "source.webm");
        var cover = Path.Combine(_dir.FullName, "thumb.jpg");
        Assert.Equal(0, (await ProcessRunner.RunAsync(_tools.Ffmpeg,
            ["-nostdin", "-y", "-f", "lavfi", "-i", "sine=d=2", "-c:a", "libopus", "-b:a", "128k", source], default)).ExitCode);
        Assert.Equal(0, (await ProcessRunner.RunAsync(_tools.Ffmpeg,
            ["-nostdin", "-y", "-f", "lavfi", "-i", "color=c=red:s=1280x720:d=1", "-frames:v", "1", cover], default)).ExitCode);

        var plan = AudioEncoder.PlanOutput(format, 192, "opus", 128);
        var path = Path.Combine(_settings.OutputFolder, "Old Artist - Old Title" + plan.Extension);
        // A genre as if added in another app: editing must keep it.
        await _encoder.EncodeAsync(source, path, plan, Before, cover, _dir.FullName, default, keptTags: Genre);
        return new TrackRecord
        {
            VideoId = "v", OriginalTitle = "Old Artist - Old Title (Official Video)",
            Status = TrackStatus.Done, FilePath = path, MatchedVideoId = "v", NeedsReview = true, Note = "Used the video's audio",
        };
    }

    private async Task<string> AudioHashAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(_tools.Ffmpeg, ["-nostdin", "-i", path, "-map", "0:a", "-c", "copy", "-f", "md5", "-"], default);
        Assert.Equal(0, result.ExitCode);
        return result.StdOut.Trim();
    }

    [Theory]
    [InlineData(AudioFormat.Original)]
    [InlineData(AudioFormat.Vorbis)]
    [InlineData(AudioFormat.Mp3)]
    [InlineData(AudioFormat.Aac)]
    [InlineData(AudioFormat.Flac)]
    public async Task Edit_rewrites_tags_keeps_audio_and_cover_and_renames(AudioFormat format)
    {
        if (!Enabled) return;
        var track = await SavedSongAsync(format);
        var oldPath = track.FilePath!;
        var audio = await AudioHashAsync(oldPath);

        await _processor.EditAsync(track, After, _settings, default);

        var newPath = Path.Combine(_settings.OutputFolder, "Daft Punk - Get Lucky" + Path.GetExtension(oldPath));
        Assert.Equal(newPath, track.FilePath);
        Assert.False(File.Exists(oldPath));
        Assert.Single(Directory.GetFiles(_settings.OutputFolder)); // no staged leftovers
        var read = await _encoder.ReadTagsAsync(newPath, default);
        Assert.Equal(After, read.Tags);
        Assert.True(read.HasCover);
        Assert.Equal("Disco", read.Other.GetValueOrDefault("genre"));
        Assert.Equal(audio, await AudioHashAsync(newPath));
        Assert.Equal(("Get Lucky", "Daft Punk, Pharrell Williams", "Random Access Memories"), (track.Title, track.Artist, track.Album));
        Assert.False(track.NeedsReview);

        // Editing again keeps the cover intact (it isn't re-cropped or lost).
        await _processor.EditAsync(track, After with { Year = 2014 }, _settings, default);
        Assert.Equal(newPath, track.FilePath);
        Assert.True((await _encoder.ReadTagsAsync(newPath, default)).HasCover);
    }

    [Fact]
    public async Task Unchanged_tags_only_mark_the_song_checked()
    {
        if (!Enabled) return;
        var track = await SavedSongAsync(AudioFormat.Original);
        var written = File.GetLastWriteTimeUtc(track.FilePath!);

        await _processor.EditAsync(track, Before, _settings, default);

        Assert.Equal(written, File.GetLastWriteTimeUtc(track.FilePath!));
        Assert.False(track.NeedsReview);
    }

    [Fact]
    public async Task A_file_open_in_a_player_is_left_untouched()
    {
        if (!Enabled) return;
        var track = await SavedSongAsync(AudioFormat.Original);
        var path = track.FilePath!;
        var bytes = await File.ReadAllBytesAsync(path);

        await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) // like most players
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _processor.EditAsync(track, After, _settings, default));
            Assert.Contains("Close it in any app", ex.Message);
        }

        Assert.Equal(path, track.FilePath);
        Assert.True(track.NeedsReview);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_settings.OutputFolder));
    }

    [Fact]
    public async Task A_name_taken_by_another_song_gets_a_number()
    {
        if (!Enabled) return;
        var track = await SavedSongAsync(AudioFormat.Original);
        var taken = Path.Combine(_settings.OutputFolder, "Daft Punk - Get Lucky.opus");
        await File.WriteAllTextAsync(taken, "another song");

        await _processor.EditAsync(track, After, _settings, default);

        Assert.Equal(Path.Combine(_settings.OutputFolder, "Daft Punk - Get Lucky (2).opus"), track.FilePath);
        Assert.Equal("another song", await File.ReadAllTextAsync(taken));
    }

    [Fact]
    public async Task A_case_only_difference_is_renamed_without_rewriting_the_file()
    {
        if (!Enabled) return;
        var track = await SavedSongAsync(AudioFormat.Original);
        var lower = Path.Combine(_settings.OutputFolder, "old artist - old title.opus");
        File.Move(track.FilePath!, lower);
        track.FilePath = lower;
        var written = File.GetLastWriteTimeUtc(lower);

        await _processor.EditAsync(track, Before, _settings, default);

        var expected = Path.Combine(_settings.OutputFolder, "Old Artist - Old Title.opus");
        Assert.Equal(expected, track.FilePath);
        Assert.Equal([expected], Directory.GetFiles(_settings.OutputFolder)); // the real name on disk, not "(2)"
        Assert.Equal(written, File.GetLastWriteTimeUtc(expected));
    }

    [Fact]
    public async Task A_changed_file_name_pattern_only_renames()
    {
        if (!Enabled) return;
        var track = await SavedSongAsync(AudioFormat.Original);
        var written = File.GetLastWriteTimeUtc(track.FilePath!);
        var settings = _settings.Clone();
        settings.FileNameTemplate = "{title} - {artist}";

        await _processor.EditAsync(track, Before, settings, default);

        Assert.Equal(Path.Combine(_settings.OutputFolder, "Old Title - Old Artist.opus"), track.FilePath);
        Assert.Equal(written, File.GetLastWriteTimeUtc(track.FilePath!));
    }

    [Fact]
    public async Task A_second_save_of_the_same_song_is_refused_while_the_first_runs()
    {
        if (!Enabled) return;
        var track = await SavedSongAsync(AudioFormat.Original);

        var first = _processor.EditAsync(track, After, _settings, default);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _processor.EditAsync(track, After with { Year = 1999 }, _settings, default));
        await first;

        Assert.Contains("already being saved", ex.Message);
        Assert.Equal(2013, (await _encoder.ReadTagsAsync(track.FilePath!, default)).Tags.Year);
        await _processor.EditAsync(track, After with { Year = 1999 }, _settings, default); // free again afterwards
    }
}
