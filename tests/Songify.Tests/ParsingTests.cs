using System.Text.Json.Nodes;
using Songify.Core.Audio;
using Songify.Core.Models;
using Songify.Core.Processing;
using Songify.Core.YouTube;

namespace Songify.Tests;

public class ParsingTests
{
    private static JsonObject Run(string text, string? pageType = null, string? videoId = null, string? videoType = null)
    {
        var run = new JsonObject { ["text"] = text };
        if (pageType is not null)
        {
            run["navigationEndpoint"] = JsonNode.Parse($$$$$"""
                {"browseEndpoint":{"browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"{{{{{pageType}}}}}"}}}}
                """);
        }
        if (videoId is not null)
        {
            run["navigationEndpoint"] = JsonNode.Parse($$$$$"""
                {"watchEndpoint":{"videoId":"{{{{{videoId}}}}}","watchEndpointMusicSupportedConfigs":{"watchEndpointMusicConfig":{"musicVideoType":"{{{{{videoType}}}}}"}}}}
                """);
        }
        return run;
    }

    private static JsonObject Item(string videoId, string videoType, string title, params JsonObject[] details) => new()
    {
        ["musicResponsiveListItemRenderer"] = new JsonObject
        {
            ["flexColumns"] = new JsonArray(
                new JsonObject { ["musicResponsiveListItemFlexColumnRenderer"] = new JsonObject { ["text"] = new JsonObject { ["runs"] = new JsonArray(Run(title, videoId: videoId, videoType: videoType)) } } },
                new JsonObject { ["musicResponsiveListItemFlexColumnRenderer"] = new JsonObject { ["text"] = new JsonObject { ["runs"] = new JsonArray(details) } } }),
            ["thumbnail"] = JsonNode.Parse("""{"musicThumbnailRenderer":{"thumbnail":{"thumbnails":[{"url":"https://yt3.example/a=w60-h60-l90-rj"},{"url":"https://yt3.example/a=w120-h120-l90-rj"}]}}}"""),
        },
    };

    [Fact]
    public void Parses_youtube_music_song_results_and_skips_music_videos()
    {
        var root = new JsonObject
        {
            ["contents"] = new JsonArray(
                Item("abc", "MUSIC_VIDEO_TYPE_ATV", "Get Lucky (feat. Pharrell Williams)",
                    Run("Daft Punk", "MUSIC_PAGE_TYPE_ARTIST"), Run(", "), Run("Pharrell Williams", "MUSIC_PAGE_TYPE_ARTIST"),
                    Run(" • "), Run("Random Access Memories", "MUSIC_PAGE_TYPE_ALBUM"), Run(" • "), Run("6:10")),
                Item("vid", "MUSIC_VIDEO_TYPE_OMV", "Get Lucky (Official Video)",
                    Run("Daft Punk", "MUSIC_PAGE_TYPE_ARTIST"), Run(" • "), Run("4:08"))),
        };

        var results = YouTubeMusicClient.ParseSearchResponse(root);

        var song = Assert.Single(results);
        Assert.Equal("abc", song.VideoId);
        Assert.Equal("Daft Punk, Pharrell Williams", song.Artists);
        Assert.Equal("Daft Punk", song.PrimaryArtist);
        Assert.Equal("Random Access Memories", song.Album);
        Assert.Equal(370, song.DurationSeconds);
        Assert.Equal("https://yt3.example/a=w544-h544-l90-rj", song.ThumbnailUrl);
    }

    [Theory]
    [InlineData("4:09", 249)]
    [InlineData("1:02:03", 3723)]
    public void Parses_durations(string text, int seconds)
    {
        Assert.True(YouTubeMusicClient.TryParseDuration(text, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Fact]
    public void Playlist_parsing_skips_private_deleted_and_non_video_entries()
    {
        var root = JsonNode.Parse("""
            {"_type":"playlist","title":"Mix","entries":[
              {"id":"aaaaaaaaaaa","title":"Artist - Song","channel":"Artist","duration":201.0},
              {"id":"bbbbbbbbbbb","title":"[Private video]","duration":null},
              {"id":"ccccccccccc","title":"[Deleted video]"},
              {"id":"UCRr1xG_2WIDs18a6cIiCxeA","title":"Some channel tab"}
            ]}
            """)!;

        var playlist = YtDlpClient.ParsePlaylist(root);

        var entry = Assert.Single(playlist.Entries);
        Assert.Equal(new VideoEntry("aaaaaaaaaaa", "Artist - Song", "Artist", 201), entry);
    }

    [Fact]
    public void Single_video_url_becomes_a_one_item_playlist()
    {
        var root = JsonNode.Parse("""{"_type":"video","id":"dQw4w9WgXcQ","title":"Song","uploader":"Up","duration":10}""")!;

        Assert.Single(YtDlpClient.ParsePlaylist(root).Entries);
    }

    [Theory]
    [InlineData("www.youtube.com", true)]
    [InlineData("music.youtube.com", true)]
    [InlineData("youtu.be", true)]
    [InlineData("notyoutube.com", false)]
    public void Recognizes_youtube_hosts(string host, bool expected)
    {
        Assert.Equal(expected, YouTubeUrls.IsYouTubeHost(host));
    }

    [Fact]
    public void Reserved_windows_names_are_escaped()
    {
        Assert.Equal("_CON", FileNamer.Sanitize("CON"));
    }

    [Fact]
    public void File_names_are_filled_and_sanitized()
    {
        var tags = new TrackTags("What? / Why: \"Now\"", "AC/DC", null, null, null);

        Assert.Equal("AC_DC - What_ _ Why_ _Now_", FileNamer.BuildBaseName("{artist} - {title}", tags, "id1"));
        Assert.Equal("Song", FileNamer.BuildBaseName("{artist} - {title}", new TrackTags("Song", null, null, null, null), "id1"));
    }

    [Fact]
    public void Year_token_is_filled_and_empty_brackets_are_dropped()
    {
        const string template = "{artist} - {title} ({year}) [{album}]";

        Assert.Equal("Daft Punk - Get Lucky (2013) [Get Lucky]",
            FileNamer.BuildBaseName(template, new TrackTags("Get Lucky", "Daft Punk", "Get Lucky", null, null, Year: 2013), "id1"));
        Assert.Equal("Daft Punk - Get Lucky",
            FileNamer.BuildBaseName(template, new TrackTags("Get Lucky", "Daft Punk", null, null, null), "id1"));
    }

    [Fact]
    public void Downloaded_album_and_year_apply_only_to_youtube_music_tracks()
    {
        var planned = new TrackTags("Get Lucky", "Daft Punk", "Get Lucky (Radio Edit)", "Daft Punk", null);
        var official = new DownloadedAudio("a.webm", "opus", 129, "Get Lucky", 2013);

        var music = TrackProcessor.WithDownloadedTags(planned, TrackSource.YouTubeMusic, official);
        Assert.Equal(("Get Lucky", 2013), (music.Album, music.Year));

        var noDescription = TrackProcessor.WithDownloadedTags(planned, TrackSource.YouTubeMusic, official with { Album = null, ReleaseYear = null });
        Assert.Equal(planned, noDescription);

        // A video's album/year may belong to another recording (Content ID) or a premiere.
        Assert.Equal(planned, TrackProcessor.WithDownloadedTags(planned, TrackSource.YouTubeVideo, official));
    }
}
