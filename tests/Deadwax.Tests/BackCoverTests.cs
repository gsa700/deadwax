using System.Text.Json;
using Deadwax.Metadata;

namespace Deadwax.Tests;

public class BackCoverTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // Neil Diamond 50 (e356201c...), as the archive files it: the primary image
    // is typed Front AND Back, the real tray scan beside it is typed Back only.
    // The /back-500 shortcut served the front for all three discs.
    [Fact]
    public void AnImageTypedFrontIsNeverTheBack()
    {
        var index = Json("""
            {"images": [
              {"id": 1, "types": ["Front", "Back"], "front": true, "back": true,
               "image": "http://x/1.jpg", "thumbnails": {"500": "http://x/1-500.jpg", "large": "http://x/1-500.jpg"}},
              {"id": 2, "types": ["Back"], "front": false, "back": false,
               "image": "http://x/2.jpg", "thumbnails": {"500": "http://x/2-500.jpg"}},
              {"id": 3, "types": ["Medium"], "front": false, "back": false, "image": "http://x/3.jpg", "thumbnails": {}}
            ]}
            """);
        Assert.Equal("http://x/2-500.jpg", BackCover.PickBack(index));
    }

    [Fact]
    public void TheFlaggedBackComesFirst()
    {
        var index = Json("""
            {"images": [
              {"types": ["Back"], "back": false, "image": "http://x/a.jpg", "thumbnails": {"500": "http://x/a-500.jpg"}},
              {"types": ["Back"], "back": true, "image": "http://x/b.jpg", "thumbnails": {"500": "http://x/b-500.jpg"}}
            ]}
            """);
        Assert.Equal("http://x/b-500.jpg", BackCover.PickBack(index));
    }

    [Fact]
    public void NoBackIsNull()
    {
        Assert.Null(BackCover.PickBack(Json("""{"images": [{"types": ["Front"], "front": true, "image": "http://x/f.jpg"}]}""")));
        Assert.Null(BackCover.PickBack(Json("""{"images": []}""")));
    }

    [Fact]
    public void WithoutA500ThumbnailTheLargeOrTheFullImage()
    {
        Assert.Equal("http://x/l.jpg", BackCover.PickBack(Json(
            """{"images": [{"types": ["Back"], "back": true, "image": "http://x/f.jpg", "thumbnails": {"large": "http://x/l.jpg"}}]}""")));
        Assert.Equal("http://x/f.jpg", BackCover.PickBack(Json(
            """{"images": [{"types": ["Back"], "back": true, "image": "http://x/f.jpg"}]}""")));
    }

    [Fact]
    public void SiblingsSameCountryThenNearestYear()
    {
        var browse = Json("""
            {"releases": [
              {"id": "mine", "country": "US", "date": "1985-05-01", "cover-art-archive": {"back": false}},
              {"id": "gb-1985", "country": "GB", "date": "1985", "cover-art-archive": {"back": true}},
              {"id": "us-2001", "country": "US", "date": "2001-03-02", "cover-art-archive": {"back": true}},
              {"id": "us-1990", "country": "US", "date": "1990", "cover-art-archive": {"back": true}},
              {"id": "us-nodate", "country": "US", "cover-art-archive": {"back": true}},
              {"id": "us-noback", "country": "US", "date": "1985", "cover-art-archive": {"back": false}}
            ]}
            """);
        Assert.Equal(["us-1990", "us-2001", "us-nodate", "gb-1985"],
            BackCover.Ranked(browse, "mine").Select(e => e.Id));
    }

    [Fact]
    public void HisOwnReleaseIsNotASibling()
    {
        var browse = Json("""{"releases": [{"id": "mine", "cover-art-archive": {"back": true}}]}""");
        Assert.Empty(BackCover.Ranked(browse, "mine"));
    }

    [Fact]
    public void AnErrorPageIsNotAnImage()
    {
        Assert.True(BackCover.LooksLikeAnImage([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.True(BackCover.LooksLikeAnImage([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.False(BackCover.LooksLikeAnImage("<html>502 Bad Gateway</html>"u8.ToArray()));
        Assert.False(BackCover.LooksLikeAnImage([]));
    }
}
