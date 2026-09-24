using Deadwax.Drive;
using Deadwax.Verify;

namespace Deadwax.Tests;

/// cdrdao's TOC files, as whipper kept them beside each rip. `deadwax check-logs`
/// runs the same comparisons over the whole library.
public class CdrdaoTocTests
{
    [Theory]
    [InlineData("52nd-street")]
    [InlineData("anthology-of-bread")]
    public void Toc_file_gives_the_same_toc_as_the_log(string name)
    {
        var fromFile = CdrdaoToc.Parse(Golden.Text(name + ".toc")).ToToc();
        var fromLog = Golden.Log(name + ".log").ToToc();

        Assert.Equal(fromLog.LeadoutLsn, fromFile.LeadoutLsn);
        Assert.Equal(fromLog.Tracks, fromFile.Tracks);
    }

    [Fact]
    public void Silence_before_track_1_is_its_pregap()
    {
        var toc = CdrdaoToc.Parse(Golden.Text("anthology-of-bread.toc"));
        Assert.Equal("0075596041423", toc.Catalog);
        Assert.Equal(33, toc.Tracks[0].SilenceSectors);
        Assert.Equal(33, toc.Tracks[0].PregapSectors);
        Assert.Equal(1 * 75 + 28, toc.Tracks[1].PregapSectors); // START 00:01:28
    }

    [Fact]
    public void Catalog_can_live_in_cd_text_only()
    {
        var toc = CdrdaoToc.Parse(Golden.Text("52nd-street.toc"));
        Assert.Null(toc.Catalog);
        Assert.Equal("888430438125", toc.DiscText?.UpcEan);
        Assert.Equal("888430438125", toc.EffectiveCatalog);
        Assert.Equal("Original Album Classics #2", toc.DiscText?.Title);

        var track1 = toc.Tracks[0];
        Assert.Equal("USSM11100749", track1.Isrc);         // subchannel
        Assert.Equal("USSM11100749", track1.Text?.Isrc);   // CD-Text: why whipper's cue lists it twice
        Assert.False(track1.PreEmphasis);
    }

    [Fact]
    public void The_two_catalogs_can_disagree()
    {
        var toc = CdrdaoToc.Parse(Golden.Text("frontiers.toc"));
        var cue = WhipperCue.Load(Golden.PathOf("frontiers.cue"));

        Assert.Equal("0082876858952", toc.Catalog);
        Assert.Equal("886919012927", toc.DiscText?.UpcEan);
        Assert.Equal(toc.Catalog, cue.Catalog);
        Assert.Equal(toc.DiscText?.UpcEan, cue.CdTextCatalog);
    }

    [Fact]
    public void A_stray_carriage_return_in_cd_text_survives()
    {
        // Skyscraper's pressing has a CR at the end of track 10's CD-Text title.
        // whipper leaves such a TITLE out of the cue rather than write a broken
        // line; to do the same, Deadwax has to see it first.
        var toc = CdrdaoToc.Parse(Golden.Text("skyscraper.toc"));
        Assert.EndsWith("\r", toc.Tracks[9].Text?.Title);
    }

    [Fact]
    public void Cue_isrcs_are_the_union_of_both_sources()
    {
        var cue = WhipperCue.Load(Golden.PathOf("52nd-street.cue"));
        Assert.Null(cue.Catalog);
        Assert.Equal("888430438125", cue.CdTextCatalog);
        Assert.Equal(["USSM11100749"], cue.IsrcsOf(1));
    }
}
