using System.Text.Json;
using Deadwax.Core;

namespace Deadwax.Tests;

public class UnboxTests
{
    [Theory]
    [InlineData("2013 - The Studio Albums 1972–1979 (Disc 2 of 6): Desperado", "Desperado")]
    [InlineData("2011 - Original Album Classics (Disc 3 of 5): Soul to Soul", "Soul to Soul")]
    [InlineData("2008 - Led Zeppelin (Disc 2 of 2) Live", "Live")]                       // no colon
    [InlineData("2003 - Music to Crash Your Car To, Vol. 2 (Disc 1 of 4): Dr. Feelgood (1989)", "Dr. Feelgood")]
    [InlineData("2017 - Neil Diamond 50: 50th Anniversary Collection (Disc 2 of 3)", null)]   // no title
    [InlineData("1977 - Rumours", null)]                                                   // not a set disc
    public void SubtitleFromTheFolder(string folder, string? expected) =>
        Assert.Equal(expected, Unbox.Subtitle(folder));

    [Fact]
    public void NormIgnoresCaseAccentsAndPunctuation()
    {
        Assert.Equal(Unbox.Norm("Shout at the Devil"), Unbox.Norm("SHOUT AT THE DEVIL!"));
        Assert.Equal(Unbox.Norm("Mötley Crüe"), Unbox.Norm("Motley Crue"));
        Assert.Equal(Unbox.Norm("Hardwired… to Self‐Destruct"), Unbox.Norm("Hardwired to Self-Destruct"));
    }

    private static JsonElement Search(string groups) => JsonDocument.Parse($$"""{"release-groups": [{{groups}}]}""").RootElement;

    // 2026-09-21: taking the first hit filed Eagles under a 1996 live album.
    [Fact]
    public void AStudioAlbumBeatsALiveOneAndTheEarliestWins()
    {
        var search = Search("""
            {"id": "live-1996", "title": "Eagles", "score": 100, "first-release-date": "1996", "secondary-types": ["Live"]},
            {"id": "studio-1972", "title": "Eagles", "score": 95, "first-release-date": "1972-06-01"},
            {"id": "studio-2003", "title": "Eagles", "score": 95, "first-release-date": "2003"}
            """);
        Assert.Equal(("studio-1972", "1972"), Unbox.Pick(search, "Eagles"));
    }

    [Fact]
    public void ALiveAlbumWinsWhenItIsTheOnlyMatch()
    {
        var search = Search("""{"id": "reo", "title": "Live: You Get What You Play For", "score": 100, "first-release-date": "1977", "secondary-types": ["Live"]}""");
        Assert.Equal(("reo", "1977"), Unbox.Pick(search, "Live: You Get What You Play For"));
    }

    [Fact]
    public void CompilationsOtherTitlesLowScoresAndNoDateAreRefused()
    {
        var search = Search("""
            {"id": "comp", "title": "Dawn to Dusk", "score": 100, "first-release-date": "1995", "secondary-types": ["Compilation"]},
            {"id": "other", "title": "Dawn to Dusk (Remastered)", "score": 98, "first-release-date": "1995"},
            {"id": "low", "title": "Dawn to Dusk", "score": 60, "first-release-date": "1995"},
            {"id": "nodate", "title": "Dawn to Dusk", "score": 100}
            """);
        Assert.Null(Unbox.Pick(search, "Dawn to Dusk"));
    }
}
