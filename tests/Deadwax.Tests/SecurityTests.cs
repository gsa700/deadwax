using System.Net.Http;
using System.Text.Json;
using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.Tests;

/// The 2026-10-10 security review:
/// each test is one of its reproductions, which must now fail to do harm.
public class SecurityTests
{
    private const string Library = "/home/x/Music";

    [Theory]
    [InlineData("..", "1979", "Album")]
    [InlineData(".", "1979", "Album")]
    [InlineData("", "1979", "Album")]
    [InlineData("Artist", "../a", "Album")]
    [InlineData("Artist", "1979", "..")]
    [InlineData("Artist", "1979", ".")]
    public void Names_from_MusicBrainz_never_leave_the_album_folder(string artist, string year, string album)
    {
        foreach (var style in Enum.GetValues<FolderStyle>())
        {
            var path = Path.GetFullPath(FileNames.AlbumPath(style, Library, artist, year, album));
            Assert.True(FileNames.IsInside(Library, path), $"{style}: {path}");
            var depth = style == FolderStyle.ArtistDashAlbum ? 1 : 2;
            Assert.Equal(depth, Path.GetRelativePath(Library, path).Split('/').Length);
        }
    }

    [Fact]
    public void Dot_names_become_underscores()
    {
        Assert.Equal("_", FileNames.Safe(".."));
        Assert.Equal("_", FileNames.Safe("."));
        Assert.Equal("_", FileNames.Safe(""));
        Assert.Equal("...", FileNames.Safe("..."));   // a real name, and harmless
        Assert.Equal("AC_DC", FileNames.Safe("AC/DC"));
    }

    [Fact]
    public void Is_inside_means_strictly_inside()
    {
        Assert.True(FileNames.IsInside(Library, Library + "/A/1979 - B"));
        Assert.False(FileNames.IsInside(Library, Library));
        Assert.False(FileNames.IsInside(Library, "/home/x/Music2/A"));
        Assert.False(FileNames.IsInside(Library, Library + "/../1979 - B"));
    }

    [Fact]
    public void Default_year_is_four_digits_or_nothing()
    {
        using var bad = JsonDocument.Parse("""{"release-group":{"first-release-date":"../x-01-01"}}""");
        Assert.Null(LibraryConventions.DefaultYear(bad.RootElement));
        using var good = JsonDocument.Parse("""{"release-group":{"first-release-date":"1977-05-01"}}""");
        Assert.Equal("1977", LibraryConventions.DefaultYear(good.RootElement));
    }

    [Fact]
    public void Line_breaks_in_titles_stay_out_of_file_names_and_the_m3u()
    {
        var name = FileNames.Track(TrackStyle.NumberDashTitle, "A", 1, "Song\n../evil\r\u2028x");
        Assert.DoesNotContain('\n', name);
        Assert.DoesNotContain('/', name);
        var m3u = Sidecars.M3u([new SidecarTrack(1, name, "t", null, 1000)]);
        Assert.Equal(3, m3u.TrimEnd('\n').Split('\n').Length);   // header, EXTINF, file
    }

    [Fact]
    public void Disc_codes_cannot_add_cue_lines()
    {
        var text = Golden.Text("skyscraper.toc")
            .Replace("CATALOG \"0603497823567\"", "CATALOG \"0603497823567\\012FILE \\\"/etc/passwd\\\" WAVE\"")
            .Replace("ISRC \"USWB22400099\"", "ISRC \"USWB22400099\\012    INDEX 01 99:00:00\"");
        var disc = CdrdaoToc.Parse(text);
        var toc = disc.ToToc();
        var tracks = toc.Tracks.Select(t => new SidecarTrack(t.Number, $"{t.Number:D2}.flac", "T", null, toc.EndLsn(t) - t.StartLsn)).ToList();
        var cue = Sidecars.Cue(disc, 0, "A", "B", tracks, "x");
        Assert.DoesNotContain("/etc/passwd", cue);
        Assert.DoesNotContain("99:00:00", cue);
        Assert.Null(disc.Catalog);
        Assert.Null(disc.Tracks[0].Isrc);
        Assert.Equal("USWB22400100", disc.Tracks[1].Isrc);   // the untouched one survives
    }

    [Fact]
    public void Codes_in_their_real_shape_are_kept()
    {
        Assert.Equal("USWB22400099", CdrdaoToc.Isrc("USWB22400099"));
        Assert.Null(CdrdaoToc.Isrc("USWB2240009"));
        Assert.Equal("0603497823567", CdrdaoToc.CatalogNumber("0603497823567"));
        Assert.Equal("886919012927", CdrdaoToc.CatalogNumber("886919012927"));   // a UPC-A in CD-Text
        Assert.Null(CdrdaoToc.CatalogNumber("06034978235a7"));
    }

    [Fact]
    public void Only_real_mbids_and_archive_urls_are_used()
    {
        Assert.True(WebLimits.IsMbid("8f6bd1e4-fbe1-4f50-aa9b-94c450ec0f11"));
        Assert.False(WebLimits.IsMbid("../../../x"));
        Assert.False(WebLimits.IsMbid("8f6bd1e4-fbe1-4f50-aa9b-94c450ec0f1/"));
        Assert.True(WebLimits.IsArchiveUrl("https://coverartarchive.org/release/x/1-500.jpg"));
        Assert.True(WebLimits.IsArchiveUrl("https://ia800.us.archive.org/x.jpg"));
        Assert.False(WebLimits.IsArchiveUrl("http://coverartarchive.org/x.jpg"));
        Assert.False(WebLimits.IsArchiveUrl("https://192.0.2.10/x.jpg"));
        Assert.False(WebLimits.IsArchiveUrl("https://notarchive.org/x.jpg"));
        Assert.False(WebLimits.IsArchiveUrl("https://archive.org.evil.example/x.jpg"));
    }

    [Fact]
    public async Task Oversized_downloads_are_refused()
    {
        var small = new ByteArrayContent(new byte[100]);
        Assert.Equal(100, (await WebLimits.ReadCappedAsync(small, 1000, default))!.Length);
        var big = new ByteArrayContent(new byte[2000]);
        big.Headers.ContentLength = null;   // no header: caught as it arrives
        Assert.Null(await WebLimits.ReadCappedAsync(new StreamContent(new MemoryStream(new byte[2000])), 1000, default));
        Assert.Null(await WebLimits.ReadCappedAsync(big, 1000, default));
    }
}
