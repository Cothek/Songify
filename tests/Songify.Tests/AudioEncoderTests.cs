using System.Buffers.Binary;
using System.Text;
using Songify.Core.Audio;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Tools;
using Songify.Core.YouTube;

namespace Songify.Tests;

public class AudioEncoderTests
{
    private static readonly TrackTags Tags = new("T", "A", "Al", "A", null);

    [Theory]
    [InlineData(AudioFormat.Mp3, ".mp3", "libmp3lame", CoverEmbedding.AttachedPicture, "MP3 256 kbps")]
    [InlineData(AudioFormat.Aac, ".m4a", "aac", CoverEmbedding.AttachedPicture, "AAC 256 kbps")]
    [InlineData(AudioFormat.Opus, ".opus", "libopus", CoverEmbedding.VorbisComment, "Opus 256 kbps")]
    [InlineData(AudioFormat.Vorbis, ".ogg", "libvorbis", CoverEmbedding.VorbisComment, "Vorbis 256 kbps")]
    [InlineData(AudioFormat.Flac, ".flac", "flac", CoverEmbedding.AttachedPicture, "FLAC")]
    [InlineData(AudioFormat.Wav, ".wav", "pcm_s16le", CoverEmbedding.None, "WAV")]
    public void Converted_formats_pick_container_codec_cover_and_bitrate(
        AudioFormat format, string extension, string codec, CoverEmbedding cover, string quality)
    {
        var plan = AudioEncoder.PlanOutput(format, 256, "opus", 129);

        Assert.Equal(extension, plan.Extension);
        Assert.Equal(codec, plan.AudioArgs[plan.AudioArgs.ToList().IndexOf("-c:a") + 1]);
        Assert.Equal(cover, plan.Cover);
        Assert.Equal(format.UsesBitrate(), plan.AudioArgs.Contains("256k"));
        Assert.Equal(quality, plan.Quality);
    }

    [Theory]
    [InlineData("opus", 128.93, ".opus", CoverEmbedding.VorbisComment, "Opus 129 kbps (original)")]
    [InlineData("mp4a.40.2", 129.5, ".m4a", CoverEmbedding.AttachedPicture, "AAC 130 kbps (original)")]
    [InlineData("aac", 128.0, ".m4a", CoverEmbedding.AttachedPicture, "AAC 128 kbps (original)")]
    [InlineData("vorbis", null, ".ogg", CoverEmbedding.VorbisComment, "Vorbis (original)")]
    [InlineData("mp3", 192.0, ".mp3", CoverEmbedding.AttachedPicture, "MP3 192 kbps (original)")]
    [InlineData("flac", null, ".mka", CoverEmbedding.None, "flac (original)")]
    [InlineData(null, null, ".mka", CoverEmbedding.None, "Unknown codec (original)")]
    public void Original_copies_the_stream_into_its_natural_container(
        string? codec, double? kbps, string extension, CoverEmbedding cover, string quality)
    {
        var plan = AudioEncoder.PlanOutput(AudioFormat.Original, 320, codec, kbps);

        Assert.Equal(extension, plan.Extension);
        Assert.Equal(["-c:a", "copy"], plan.AudioArgs.Take(2));
        Assert.Equal(extension == ".mp3", plan.AudioArgs.Contains("-id3v2_version"));
        Assert.DoesNotContain("320k", plan.AudioArgs);
        Assert.Equal(cover, plan.Cover);
        Assert.Equal(quality, plan.Quality);
    }

    [Fact]
    public void Attached_cover_args_map_the_picture_and_write_tags_inline()
    {
        var plan = AudioEncoder.PlanOutput(AudioFormat.Mp3, 256, "opus", 129);
        var args = AudioEncoder.BuildArgs("in.webm", "out", plan, Tags, "cover.jpg", metadataFile: null);

        Assert.Contains("1:v:0", args);
        Assert.Contains("attached_pic", args);
        Assert.Contains("title=T", args);
        Assert.Equal("out", args[^1]);
    }

    [Fact]
    public void Ogg_args_read_tags_from_the_metadata_file_and_drop_source_stream_tags()
    {
        var plan = AudioEncoder.PlanOutput(AudioFormat.Original, 320, "opus", 129);
        var args = AudioEncoder.BuildArgs("in.webm", "out.opus", plan, Tags, attachedCover: null, "tags.ffmeta");

        Assert.Equal(["-f", "ffmetadata", "-i", "tags.ffmeta"], args.Skip(5).Take(4));
        Assert.Equal("1", args[args.IndexOf("-map_metadata") + 1]);
        Assert.Equal("-1", args[args.IndexOf("-map_metadata:s:a") + 1]);
        Assert.DoesNotContain("-metadata", args);
        Assert.DoesNotContain("attached_pic", args);
    }

    [Fact]
    public void Metadata_file_escapes_special_characters_and_carries_the_picture()
    {
        var text = AudioEncoder.BuildFfMetadata(new TrackTags("a=b;c#d\\e\nf", null, null, null, null, Year: 1987), [1, 2, 3]);

        Assert.StartsWith(";FFMETADATA1\n", text);
        Assert.Contains("date=1987\n", text);
        Assert.Contains("title=a\\=b\\;c\\#d\\\\e\\\nf\n", text);
        Assert.Contains("METADATA_BLOCK_PICTURE=AQID\n", text);
        Assert.DoesNotContain("artist=", text);
    }

    [Fact]
    public void Picture_block_follows_the_flac_layout()
    {
        var block = AudioEncoder.FlacPictureBlock([0xFF, 0xD8, 0xFF], 544, 544);
        uint At(int offset) => BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(offset));

        Assert.Equal(3u, At(0)); // front cover
        Assert.Equal(10u, At(4));
        Assert.Equal("image/jpeg", Encoding.ASCII.GetString(block, 8, 10));
        Assert.Equal(0u, At(18)); // description
        Assert.Equal(544u, At(22));
        Assert.Equal(544u, At(26));
        Assert.Equal(3u, At(38)); // data length
        Assert.Equal(45, block.Length);
    }

    [Fact]
    public void Download_report_is_parsed_from_yt_dlp_json()
    {
        var parsed = YtDlpClient.ParseDownload("""{"filepath": "C:\\w\\source.webm", "acodec": "opus", "abr": 128.93}""");
        Assert.Equal(new DownloadedAudio(@"C:\w\source.webm", "opus", 128.93), parsed);

        Assert.Equal(new DownloadedAudio("a.webm", "opus", null, "Get Lucky", 2013),
            YtDlpClient.ParseDownload("""{"filepath": "a.webm", "acodec": "opus", "album": "Get Lucky", "release_year": 2013}"""));
        Assert.Equal(new DownloadedAudio("a.webm", null, null),
            YtDlpClient.ParseDownload("""{"filepath": "a.webm", "album": " ", "release_year": 0}"""));

        // yt-dlp's %()j escapes non-ASCII.
        Assert.Equal(@"C:\Users\Señor 日本\source.webm",
            YtDlpClient.ParseDownload("""{"filepath": "C:\\Users\\Se\u00f1or \u65e5\u672c\\source.webm"}""")!.Path);

        Assert.Equal(new DownloadedAudio("x.m4a", null, null), YtDlpClient.ParseDownload("""{"filepath": "x.m4a", "abr": null}"""));
        Assert.Null(YtDlpClient.ParseDownload("not json"));
        Assert.Null(YtDlpClient.ParseDownload("[]"));
        Assert.Null(YtDlpClient.ParseDownload("""{"filepath": 5}"""));
        Assert.Null(YtDlpClient.ParseDownload(""));
    }

    /// <summary>Runs the real ffmpeg (opt-in: SONGIFY_INTEGRATION=1, tools already downloaded).</summary>
    [Fact]
    public async Task Original_opus_keeps_audio_untouched_and_embeds_tags_and_cover()
    {
        if (Environment.GetEnvironmentVariable("SONGIFY_INTEGRATION") != "1") return;
        var tools = new ToolManager(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Songify", "tools"),
            new HttpClient());
        var work = Directory.CreateTempSubdirectory("songify-enc-").FullName;
        try
        {
            var source = Path.Combine(work, "source.webm");
            var cover = Path.Combine(work, "cover.jpg");
            Assert.Equal(0, (await ProcessRunner.RunAsync(tools.Ffmpeg,
                ["-nostdin", "-y", "-f", "lavfi", "-i", "sine=d=2", "-c:a", "libopus", "-b:a", "128k", source], default)).ExitCode);
            Assert.Equal(0, (await ProcessRunner.RunAsync(tools.Ffmpeg,
                ["-nostdin", "-y", "-f", "lavfi", "-i", "color=c=red:s=1280x720:d=1", "-frames:v", "1", cover], default)).ExitCode);

            var output = Path.Combine(work, "out.opus");
            var plan = AudioEncoder.PlanOutput(AudioFormat.Original, 320, "opus", 128);
            await new AudioEncoder(tools).EncodeAsync(source, output, plan,
                new TrackTags("Señor = Song", "Artist", "Album", "Artist", "https://music.youtube.com/watch?v=x"), cover, work, default);

            var probe = await ProcessRunner.RunAsync(tools.Ffprobe,
                ["-v", "error", "-show_entries", "stream=codec_name,width,height:stream_disposition=attached_pic:stream_tags=title,artist,album",
                 "-of", "compact", output], default);
            Assert.Equal(0, probe.ExitCode);
            Assert.Contains("codec_name=opus", probe.StdOut);
            Assert.Contains("tag:title=Señor = Song", probe.StdOut);
            Assert.Contains("tag:album=Album", probe.StdOut);
            Assert.Contains("codec_name=mjpeg|width=544|height=544|disposition:attached_pic=1", probe.StdOut);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
