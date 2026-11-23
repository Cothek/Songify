using Songify.Core.Matching;
using Songify.Core.YouTube;

namespace Songify.Tests;

public class TrackMatcherTests
{
    private static MusicTrack Song(string title, string artists, int? seconds, string? album = null, string id = "x") =>
        new(id, title, artists, artists.Split(',')[0], album, seconds, null);

    [Fact]
    public void Prefers_the_version_whose_length_matches_the_video()
    {
        var source = TitleParser.Parse("Daft Punk - Get Lucky (Official Video) feat. Pharrell Williams", "convar HUN");
        var album = Song("Get Lucky (feat. Pharrell Williams and Nile Rodgers)", "Daft Punk, Pharrell Williams & Nile Rodgers", 370, "Random Access Memories", "album");
        var radio = Song("Get Lucky (Radio Edit - feat. Pharrell Williams and Nile Rodgers)", "Daft Punk, Pharrell Williams & Nile Rodgers", 249, "Get Lucky", "radio");

        var best = TrackMatcher.Best(source, 248, [album, radio]);

        Assert.Equal("radio", best!.Track.VideoId);
        Assert.True(best.Score > 0.9);
    }

    [Fact]
    public void Penalizes_live_and_remix_versions_the_video_did_not_ask_for()
    {
        var source = TitleParser.Parse("Adele - Hello (Official Video)", null);
        var studio = Song("Hello", "Adele", 295, "25", "studio");
        var live = Song("Hello (Live at the BBC)", "Adele", 295, "Live", "live");
        var remix = Song("Hello (Remix)", "Adele", 295, null, "remix");

        Assert.True(TrackMatcher.Score(source, 366, studio) - TrackMatcher.Score(source, 366, live) >= 0.3);
        Assert.True(TrackMatcher.Score(source, 366, studio) - TrackMatcher.Score(source, 366, remix) >= 0.3);
    }

    [Fact]
    public void Accepts_live_when_the_video_is_live()
    {
        var source = TitleParser.Parse("Adele - Hello (Live at the BBC)", null);
        var live = Song("Hello (Live at the BBC)", "Adele", 300, "Live at the BBC");

        Assert.True(TrackMatcher.Score(source, 300, live) > 0.9);
    }

    [Fact]
    public void Wrong_artist_with_same_title_scores_below_default_threshold()
    {
        var source = TitleParser.Parse("Adele - Hello", null);
        var other = Song("Hello", "Lionel Richie", 250);

        Assert.True(TrackMatcher.Score(source, 295, other) < 0.70);
    }

    [Fact]
    public void Handles_title_artist_swapped_uploads()
    {
        var source = TitleParser.Parse("Hello - Adele", null);
        var song = Song("Hello", "Adele", 295);

        Assert.True(TrackMatcher.Score(source, 295, song) > 0.85);
    }

    [Theory]
    [InlineData(200, 200, 1.0)]
    [InlineData(200, 204, 1.0)]
    [InlineData(200, 230, 0.0)] // album track much longer than video: wrong version
    [InlineData(260, 200, 0.7)] // video has a ~60s intro: tolerated
    public void Duration_score(int video, int candidate, double expected)
    {
        Assert.Equal(expected, TrackMatcher.DurationScore(video, candidate), 1);
    }

    [Fact]
    public void Artist_similarity_finds_primary_artist_in_a_credit_list()
    {
        Assert.Equal(1, TrackMatcher.ArtistSimilarity("Daft Punk ft. Pharrell", "Daft Punk, Pharrell Williams & Nile Rodgers"));
        Assert.Equal(1, TrackMatcher.ArtistSimilarity("Beyoncé", "Beyonce"));
        Assert.Equal(1, TrackMatcher.ArtistSimilarity("Earth, Wind & Fire", "Earth, Wind & Fire"));
    }

    [Fact]
    public void Artist_similarity_does_not_treat_a_longer_name_as_the_same_artist()
    {
        Assert.True(TrackMatcher.ArtistSimilarity("Queen", "Queen Latifah") < 0.5);
        // Could be a band called "Earth" or a band with commas in its name: likely, not certain.
        Assert.True(TrackMatcher.ArtistSimilarity("Earth, Wind & Fire", "Earth") <= 0.8);
    }

    [Fact]
    public void Artist_similarity_handles_credits_that_split_to_nothing()
    {
        // "X" and "and" are separators to the splitter; must not throw.
        Assert.InRange(TrackMatcher.ArtistSimilarity("Foo", "X"), 0, 1);
        Assert.InRange(TrackMatcher.ArtistSimilarity("Foo", "and"), 0, 1);
    }

    [Fact]
    public void Shared_words_are_not_the_same_title()
    {
        var source = TitleParser.Parse("Taylor Swift - Love", null);
        var other = Song("Love Story", "Taylor Swift", 235);

        Assert.True(TrackMatcher.Score(source, 235, other) < 0.70);
    }

    [Fact]
    public void Title_alone_is_not_enough_without_an_artist()
    {
        var source = TitleParser.Parse("Hello", null);
        var song = Song("Hello", "Adele", 295);

        Assert.True(TrackMatcher.Score(source, 295, song) < 0.70);
    }

    [Fact]
    public void Artist_name_does_not_count_as_a_version_tag()
    {
        var source = TitleParser.Parse("Live - Lightning Crashes", null);
        var song = Song("Lightning Crashes", "Live", 325);

        Assert.True(TrackMatcher.Score(source, 325, song) > 0.9);
    }
}
