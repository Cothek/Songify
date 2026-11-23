using Songify.Core.Matching;

namespace Songify.Tests;

public class TitleParserTests
{
    [Theory]
    [InlineData("Daft Punk - Get Lucky (Official Video) feat. Pharrell Williams and Nile Rodgers", "Daft Punk", "Get Lucky")]
    [InlineData("Rick Astley - Never Gonna Give You Up (Official Music Video)", "Rick Astley", "Never Gonna Give You Up")]
    [InlineData("Adele – Hello [Official Video] [HD]", "Adele", "Hello")]
    [InlineData("Artist - \"Quoted Song\" | Official Lyric Video", "Artist", "Quoted Song")]
    [InlineData("Band - Song | Napalm Records", "Band", "Song")]
    [InlineData("Artist - Song #shorts #music", "Artist", "Song")]
    public void Splits_artist_and_title_and_strips_noise(string video, string artist, string title)
    {
        var parsed = TitleParser.Parse(video, "Some Channel");

        Assert.Equal(artist, parsed.Artist);
        Assert.StartsWith(title, parsed.Title);
        Assert.False(parsed.ArtistFromChannel);
    }

    [Fact]
    public void Keeps_brackets_that_describe_a_different_version()
    {
        var parsed = TitleParser.Parse("Artist - Song (Live at Wembley) [Official Video]", null);

        Assert.Equal("Song (Live at Wembley)", parsed.Title);
    }

    [Theory]
    [InlineData("AdeleVEVO", "Adele")]
    [InlineData("Daft Punk - Topic", "Daft Punk")]
    [InlineData("Coldplay", "Coldplay")]
    public void Falls_back_to_channel_for_artist(string channel, string artist)
    {
        var parsed = TitleParser.Parse("Hello (Official Video)", channel);

        Assert.Equal(artist, parsed.Artist);
        Assert.Equal("Hello", parsed.Title);
        Assert.True(parsed.ArtistFromChannel);
    }

    [Fact]
    public void No_artist_when_no_separator_and_no_channel()
    {
        var parsed = TitleParser.Parse("Just A Title", null);

        Assert.Null(parsed.Artist);
        Assert.Equal("Just A Title", parsed.Query);
    }
}
