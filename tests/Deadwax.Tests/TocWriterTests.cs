using Deadwax.Drive;

namespace Deadwax.Tests;

/// CdrdaoToc.Write must give back exactly what cdrdao wrote: the .toc beside a
/// Deadwax-read rip is then the same file whipper and cdrdao left beside
/// theirs, and `deadwax check-logs` reads all of them alike.
public class TocWriterTests
{
    [Theory]
    [InlineData("52nd-street.toc")]          // catalog from CD-Text only, SIZE_INFO
    [InlineData("anthology-of-bread.toc")]   // SILENCE before track 1
    [InlineData("back-in-black.toc")]
    [InlineData("far-beyond-driven.toc")]    // COPY permitted
    [InlineData("frontiers.toc")]            // CATALOG and CD-Text, DISC_ID, GENRE, UPC_EAN
    [InlineData("megadeth-2026.toc")]        // ISRCs, an empty PERFORMER
    [InlineData("neil-diamond-50-1.toc")]    // CLOSED
    [InlineData("skyscraper.toc")]           // a CR inside a title
    [InlineData("tango-in-the-night.toc")]   // a CR at the end of the disc title
    [InlineData("volume-one.toc")]           // SILENCE and START on track 1
    public void A_toc_file_round_trips_byte_for_byte(string name)
    {
        var text = Golden.Text(name);
        Assert.Equal(text, CdrdaoToc.Parse(text).Write());
    }

    /// The whole library, when DEADWAX_TOC_SWEEP names a folder: every .toc
    /// under it must round-trip. Not run in CI (no library there).
    [Fact]
    public void Every_toc_in_the_library_round_trips()
    {
        var root = Environment.GetEnvironmentVariable("DEADWAX_TOC_SWEEP");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { Console.WriteLine("DEADWAX_TOC_SWEEP not set; nothing to sweep"); return; }
        var files = Directory.EnumerateFiles(root, "*.toc", SearchOption.AllDirectories).ToList();
        var failures = new List<string>();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            string back;
            try { back = CdrdaoToc.Parse(text).Write(); }
            catch (Exception e) { failures.Add($"{f}: {e.Message}"); continue; }
            if (back != text) failures.Add(f);
        }
        Console.WriteLine($"{files.Count} files, {failures.Count} differ");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }

    [Fact]
    public void Msf_is_zero_padded()
    {
        Assert.Equal("00:00:33", CdrdaoToc.Msf(33));
        Assert.Equal("02:40:22", CdrdaoToc.Msf((2 * 60 + 40) * 75 + 22));
        Assert.Equal("79:59:74", CdrdaoToc.Msf((79 * 60 + 59) * 75 + 74));
    }

    [Fact]
    public void Quotes_and_backslashes_are_escaped_like_cdrdao()
    {
        var text = new CdText("Say \"Hi\" \\ Bye", null, null, null);
        var toc = CdrdaoToc.Parse("CD_DA\n\nCD_TEXT {\n  LANGUAGE 0 {\n    TITLE \"Say \\\"Hi\\\" \\\\ Bye\"\n  }\n}\n\n// Track 1\nTRACK AUDIO\nNO COPY\nNO PRE_EMPHASIS\nTWO_CHANNEL_AUDIO\nFILE \"data.wav\" 0 00:10:00\n\n");
        Assert.Equal(text.Title, toc.DiscText?.Title);
        Assert.Equal(toc.Write(), CdrdaoToc.Parse(toc.Write()).Write());
        Assert.Contains("TITLE \"Say \\\"Hi\\\" \\\\ Bye\"", toc.Write());
    }
}
