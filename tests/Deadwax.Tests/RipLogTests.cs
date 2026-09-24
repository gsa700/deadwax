using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Output;
using Deadwax.Verify;

namespace Deadwax.Tests;

public class RipLogTests
{
    [Theory]
    [InlineData("52nd-street.log")]
    [InlineData("turnstiles.log")]
    [InlineData("anthology-of-bread.log")]
    public void Whippers_hash_line_checks_out_by_the_same_rule(string file) =>
        Assert.True(RipLog.Verify(Golden.Text(file)));

    [Fact]
    public void A_deadwax_log_verifies_and_reads_back_through_the_same_reader()
    {
        var whipper = Golden.Log("52nd-street.log");
        var toc = whipper.ToToc();
        var disc = new LogDisc(
            "PIONEER BD-RW   BDR-209D (revision 1.10)", "libcdio-paranoia 10.2+2.0.2 (libcdio 2.3.0)", 667, "cdrdao 1.2.6", false,
            "Billy Joel", "52nd Street", DiscIds.Cddb(toc), DiscIds.MusicBrainz(toc), DiscIds.MusicBrainzAttachUrl(toc),
            "8e79627f-0c4e-4bb6-85f3-92fbbc5430bc", toc);
        var tracks = whipper.Tracks.Select(t => new LogTrack(
            t.Number, $"Billy Joel/1978 - 52nd Street/Billy Joel - {t.Number:D2} - x.flac", 1.0, false, 9.5,
            t.TestCrc!.Value, t.CopyCrc!.Value, 0, 0,
            new LogAccurateRip(false, 0, 0, null),
            new LogAccurateRip(true, t.V2!.LocalCrc!.Value, t.V2.Confidence!.Value, t.V2.RemoteCrc),
            "Copy OK")).ToList();

        var text = RipLog.Write(disc, tracks, "0.1", DateTimeOffset.Parse("2026-09-24T12:00:00Z"));
        Assert.True(RipLog.Verify(text));
        Assert.False(RipLog.Verify(text.Replace("Copy OK", "Copy 0K")));   // any edit breaks it

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text);
            var back = WhipperLog.Load(path);
            Assert.Equal(whipper.CddbId, back.CddbId);
            Assert.Equal(whipper.MusicBrainzId, back.MusicBrainzId);
            Assert.Equal(whipper.MusicBrainzToc, back.MusicBrainzToc);
            Assert.Equal(toc.Tracks, back.ToToc().Tracks);
            Assert.Equal(toc.LeadoutLsn, back.ToToc().LeadoutLsn);
            Assert.Equal(whipper.Tracks.Select(t => (t.Number, t.CopyCrc, t.V2!.LocalCrc, t.Status)),
                         back.Tracks.Select(t => (t.Number, t.CopyCrc, t.V2!.LocalCrc, t.Status)));
            Assert.Contains("All tracks accurately ripped", text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
