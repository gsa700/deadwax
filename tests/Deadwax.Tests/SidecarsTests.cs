using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.Tests;

/// Gate G4, sidecars: `deadwax check-sidecars` runs this over the library.
public class SidecarsTests
{
    private static List<SidecarTrack> Tracks(CdrdaoToc disc, string artist, string[] titles)
    {
        var toc = disc.ToToc();
        return toc.Tracks.Select(t => new SidecarTrack(
            t.Number, FileNames.Track(artist, t.Number, titles[t.Number - 1]), titles[t.Number - 1], null,
            toc.EndLsn(t) - t.StartLsn)).ToList();
    }

    private static readonly string[] FiftySecondStreet =
    [
        "Big Shot", "Honesty", "My Life", "Zanzibar", "Stiletto", "Rosalinda's Eyes",
        "Half A Mile Away", "Until The Night", "52nd Street",
    ];

    [Fact]
    public void M3u_is_whippers_exactly()
    {
        var disc = CdrdaoToc.Parse(Golden.Text("52nd-street.toc"));
        Assert.Equal(Golden.Text("52nd-street.m3u"), Sidecars.M3u(Tracks(disc, "Billy Joel", FiftySecondStreet)));
    }

    [Fact]
    public void Cue_puts_each_pregap_at_the_end_of_the_previous_file()
    {
        var disc = CdrdaoToc.Parse(Golden.Text("anthology-of-bread.toc"));
        var titles = Enumerable.Range(1, 20).Select(n => $"T{n}").ToArray();
        var cue = Sidecars.Cue(disc, DiscIds.Cddb(disc.ToToc()), "Bread", "Anthology of Bread", Tracks(disc, "Bread", titles), "test");
        var lines = cue.Split('\n');

        // whipper's cue for the same disc has these exact index lines.
        var whipper = Golden.Text("anthology-of-bread.cue").Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("INDEX ")).ToList();
        Assert.Equal(whipper, lines.Select(l => l.Trim()).Where(l => l.StartsWith("INDEX ")).ToList());

        Assert.Contains("REM DISCID 0E0DE114", lines);
        Assert.Contains("CATALOG 0075596041423", lines);
        Assert.Contains("    PREGAP 00:00:33", lines);   // the stretch before track 1, which whipper left out here
        var track2 = Array.IndexOf(lines, "  TRACK 02 AUDIO");
        Assert.Equal("    INDEX 00 03:09:47", lines[track2 + 2]);
        Assert.StartsWith("FILE \"Bread - 02 - T2.flac\"", lines[track2 + 3]);
    }

    [Fact]
    public void Names_never_hold_a_slash_or_a_double_quote()
    {
        Assert.Equal("AC_DC - 06 - Back in Black.flac", FileNames.Track("AC/DC", 6, "Back in Black"));
        Assert.Equal("Billy Joel - 03 - The Downeaster 'Alexa'.flac", FileNames.Track("Billy Joel", 3, "The Downeaster \"Alexa\""));
    }
}
