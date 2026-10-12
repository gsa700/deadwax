using Deadwax.Metadata;
using Deadwax.Drive;
using SkiaSharp;

namespace Deadwax.Tests;

/// 0.2.14, from one evening's rip (2026-10-11): a release edited on MusicBrainz
/// while Deadwax held an old copy, a front uploaded minutes before the rip, and
/// a disc whose ID sat on another edition.
public class MusicBrainzFixesTests
{
    [Fact]
    public void Cached_release_expires()
    {
        var path = Path.Combine(Path.GetTempPath(), $"deadwax-release-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{}");
        try
        {
            var now = DateTime.UtcNow;
            Assert.True(MusicBrainzClient.CacheUsable(path, now));
            File.SetLastWriteTimeUtc(path, now - TimeSpan.FromMinutes(40));   // the copy that hid his new disc 2
            Assert.False(MusicBrainzClient.CacheUsable(path, now));
            Assert.False(MusicBrainzClient.CacheUsable(path + ".missing", now));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Attach_link_finds_the_chosen_release()
    {
        // Lightning to the Nations, disc 2 (the 2022 Silver Lining set).
        var toc = new Toc(
            [new TocTrack(1, 0, true), new TocTrack(2, 19143, true), new TocTrack(3, 35011, true), new TocTrack(4, 50643, true),
             new TocTrack(5, 67947, true), new TocTrack(6, 83549, true), new TocTrack(7, 102884, true)], 122521);
        var plain = DiscIds.MusicBrainzAttachUrl(toc);
        Assert.StartsWith("https://musicbrainz.org/cdtoc/attach?toc=1+7+122671+150+19293+35161+50793+68097+83699+103034&tracks=7&id=", plain);
        Assert.DoesNotContain("filter-release", plain);
        // The release's own ID in the page's "Release title or MBID" search: tried by him, it lists just that release.
        Assert.EndsWith("&filter-release.query=c2cb10cc-39ef-4b21-856b-f7ba67cbb84e", DiscIds.MusicBrainzAttachUrl(toc, "c2cb10cc-39ef-4b21-856b-f7ba67cbb84e"));
        Assert.DoesNotContain("filter-release", DiscIds.MusicBrainzAttachUrl(toc, "not an id&x=1"));
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(new SKColor(200, 80, 30));
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    [Fact]
    public void Full_size_front_is_scaled_to_500_on_its_longer_side()
    {
        var scaled = CoverArt.ScaleToFit(Jpeg(3100, 2789), 500)!;
        using var back = SKBitmap.Decode(scaled);
        Assert.Equal(500, back.Width);
        Assert.Equal(450, back.Height);   // the shape kept: his front came out 500x450

        var small = Jpeg(480, 300);
        Assert.Same(small, CoverArt.ScaleToFit(small, 500));   // never scaled up
        Assert.Null(CoverArt.ScaleToFit("not an image"u8.ToArray(), 500));
    }
}
