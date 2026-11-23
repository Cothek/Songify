using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Processing;
using Xunit.Abstractions;

namespace Songify.Tests;

/// <summary>
/// Hits real YouTube and downloads real tools. Opt-in: set SONGIFY_INTEGRATION=1.
/// Uses the shared tools folder (%LOCALAPPDATA%\Songify\tools) and a temp data/output folder.
/// </summary>
public class EndToEndTests(ITestOutputHelper output)
{
    // An unofficial re-upload of a music video: should be swapped for the album track
    // (Get Lucky (Radio Edit), single "Get Lucky", released 2013).
    private const string MusicVideoUrl = "https://www.youtube.com/watch?v=CCHdMIEGaaM";

    [Theory]
    [InlineData(AudioFormat.Mp3, ".mp3", "MP3 192 kbps", true)]
    [InlineData(AudioFormat.Aac, ".m4a", "AAC 192 kbps", true)]
    [InlineData(AudioFormat.Opus, ".opus", "Opus 192 kbps", true)]
    [InlineData(AudioFormat.Vorbis, ".ogg", "Vorbis 192 kbps", true)]
    [InlineData(AudioFormat.Flac, ".flac", "FLAC", true)]
    [InlineData(AudioFormat.Wav, ".wav", "WAV", false)]
    [InlineData(AudioFormat.Original, ".opus", "Opus ", true)]
    public async Task Music_video_is_saved_from_youtube_music_with_tags(
        AudioFormat format, string extension, string quality, bool hasCover)
    {
        if (Environment.GetEnvironmentVariable("SONGIFY_INTEGRATION") != "1") return;

        var temp = Directory.CreateTempSubdirectory("songify-e2e-");
        var toolsRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Songify");
        var paths = new AppPaths(Path.Combine(temp.FullName, "data"), toolsRoot);
        Directory.CreateDirectory(paths.DataDir);
        JsonFile.Save(paths.SettingsFile, new AppSettings
        {
            OutputFolder = Path.Combine(temp.FullName, "out"),
            Format = format,
            BitrateKbps = 192,
            Playlists = [MusicVideoUrl],
            LastToolUpdateCheck = DateTimeOffset.Now,
        });
        AppLog.Line += output.WriteLine;
        try
        {
            TrackRecord? track = null;
            await using (var service = new SongifyService(paths))
            {
                await service.StartAsync();
                Assert.True(service.ToolsReady);

                var deadline = DateTime.UtcNow.AddMinutes(5);
                while (DateTime.UtcNow < deadline)
                {
                    track = service.GetTracks().SingleOrDefault();
                    if (track?.Status is TrackStatus.Done or TrackStatus.Failed) break;
                    await Task.Delay(500);
                }
            }

            Assert.NotNull(track);
            output.WriteLine($"{track.Status} {track.Source} {track.Artist} - {track.Title} [{track.Album}] {track.MatchScore} {track.Quality} {track.Note} → {track.FilePath}");
            Assert.Equal(TrackStatus.Done, track.Status);
            Assert.Equal(TrackSource.YouTubeMusic, track.Source);
            Assert.True(File.Exists(track.FilePath));
            Assert.EndsWith(extension, track.FilePath);
            Assert.StartsWith(quality, track.Quality);
            Assert.StartsWith("Daft Punk - ", Path.GetFileName(track.FilePath));
            Assert.Equal("Get Lucky", track.Album); // official album, not the search result's track title
            Assert.True(File.Exists(paths.LibraryFile), "library is saved on dispose");

            // What a music player sees in the file.
            var probe = await ProcessRunner.RunAsync(Path.Combine(toolsRoot, "tools", "ffprobe.exe"),
                ["-v", "error", "-show_entries", "format_tags:stream_tags:stream=codec_type,width:stream_disposition=attached_pic",
                 "-of", "flat", track.FilePath!], default);
            output.WriteLine(probe.StdOut);
            var tags = probe.StdOut.ToLowerInvariant();
            Assert.Contains("title=\"get lucky (radio edit", tags);
            Assert.Contains("artist=\"daft punk", tags);
            Assert.Contains("date=\"2013\"", tags);
            Assert.Contains("album=\"get lucky\"", tags);
            Assert.Equal(hasCover, tags.Contains("attached_pic=1"));
        }
        finally
        {
            AppLog.Line -= output.WriteLine;
            AppLog.FilePath = null;
            try { temp.Delete(recursive: true); } catch (IOException) { }
        }
    }
}
