using Deadwax.Core;
using Deadwax.Drive;
using Deadwax.Metadata;

namespace Deadwax.Tests;

public class SelfDescribedTests
{
    private static PreparedDisc Disc()
    {
        var toc = new Toc([new TocTrack(1, 0, true), new TocTrack(2, 20000, true), new TocTrack(3, 60000, true)], 90000);
        return new PreparedDisc("/dev/none", null, toc, 667, "DISCID", 0x1234u, null, []);
    }

    [Fact]
    public void TagsLikeAReleaseWithoutMusicBrainzIds()
    {
        var d = new DiscDescription("Some Band", "Live at the Bar", "1994", ["Opener", "Middle", "Closer"]);
        using var plan = RipSession.PlanSelfDescribed(Disc(), d);
        Assert.True(plan.IsSelfDescribed);
        Assert.Null(plan.ReleaseId);
        Assert.Equal("Some Band", plan.AlbumArtist);
        Assert.Equal("Live at the Bar", plan.DiscTitle);
        Assert.Equal("1994", plan.DefaultYear);
        Assert.Equal(["Opener", "Middle", "Closer"], plan.Tracks.Select(t => t.Title));

        var tags = ReleaseTags.ForTrack(plan.Root, plan.Medium, 2, "DISCID", null).ToDictionary(t => t.Key, t => t.Value);
        Assert.Equal("Middle", tags["TITLE"]);
        Assert.Equal("Some Band", tags["ARTIST"]);
        Assert.Equal("Some Band", tags["ALBUMARTIST"]);
        Assert.Equal("2", tags["TRACKNUMBER"]);
        Assert.Equal("3", tags["TRACKTOTAL"]);
        Assert.Equal("1994", tags["DATE"]);
        Assert.Equal("DISCID", tags["MUSICBRAINZ_DISCID"]);
        Assert.DoesNotContain(tags.Keys, k => k.StartsWith("MUSICBRAINZ_") && k != "MUSICBRAINZ_DISCID");
    }

    [Fact]
    public void RefusesTheWrongTrackCountAndBlankFields()
    {
        Assert.Throws<RipException>(() => RipSession.PlanSelfDescribed(Disc(), new DiscDescription("A", "B", "2000", ["x"])));
        Assert.NotNull(new DiscDescription("", "B", "2000", ["x"]).Problem());
        Assert.NotNull(new DiscDescription("A", "B", "20", ["x"]).Problem());
        Assert.NotNull(new DiscDescription("A", "B", "2000", ["x", " "]).Problem());
        Assert.Null(new DiscDescription("A", "B", "2000", ["x"]).Problem());
    }
}
