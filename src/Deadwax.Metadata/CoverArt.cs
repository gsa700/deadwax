using System.Net;

namespace Deadwax.Metadata;

/// The front cover from the Cover Art Archive, as whipper's `-C file` saved it:
/// the 500-pixel front, which is what nearly every cover.jpg in the library is.
///
/// The archive is flaky in a particular way: one URL answered 200, 500, 500
/// within seconds (music-unbox, 2026-09-23), and a failed fetch read as "no
/// cover" kept a box's slipcase where the album's front belonged. So every
/// fetch retries with a growing pause, and only a 404 means "none".
///
/// The other failure is the archive being down for everyone (its image host
/// reset every connection for most of an evening, 2026-10-06): then five tries
/// with a pause between them, for two URLs, is minutes of waiting for nothing.
/// A caller with somewhere better to be asks for one attempt.
public static class CoverArt
{
    public const int DefaultAttempts = 5;

    public static async Task<byte[]?> FrontAsync(HttpClient http, string releaseId, string? releaseGroupId, CancellationToken ct = default, int attempts = DefaultAttempts)
    {
        if (!WebLimits.IsMbid(releaseId)) return releaseGroupId is null ? null : await GroupFrontAsync(http, releaseGroupId, ct, attempts);
        return await FetchAsync(http, $"https://coverartarchive.org/release/{releaseId}/front-500", ct, attempts)
               ?? await FullFrontAsync(http, releaseId, ct, attempts)
               ?? (!WebLimits.IsMbid(releaseGroupId) ? null
                   : await FetchAsync(http, $"https://coverartarchive.org/release-group/{releaseGroupId}/front-500", ct, attempts));
    }

    /// The release group's front only: for a box disc refiled as its album,
    /// where no single release of that album is known.
    public static Task<byte[]?> GroupFrontAsync(HttpClient http, string releaseGroupId, CancellationToken ct = default, int attempts = DefaultAttempts) =>
        !WebLimits.IsMbid(releaseGroupId) ? Task.FromResult<byte[]?>(null) : FetchAsync(http, $"https://coverartarchive.org/release-group/{releaseGroupId}/front-500", ct, attempts);

    /// The full-size front, scaled to the 500 pixels the library uses. The
    /// archive makes its 500-pixel copy some minutes to an hour after an
    /// upload; until then front-500 is "not found" while the front itself is
    /// there. Without this the release group's front was used instead, a
    /// different edition's sleeve (2026-10-11, a front uploaded minutes before
    /// the rip).
    private static async Task<byte[]?> FullFrontAsync(HttpClient http, string releaseId, CancellationToken ct, int attempts)
    {
        var full = await FetchAsync(http, $"https://coverartarchive.org/release/{releaseId}/front", ct, attempts);
        return full is null ? null : ScaleToFit(full, 500);
    }

    /// The most pixels a cover may have to be decoded at all: 50 million
    /// (about 200 MB decoded; the archive's largest real scans are about
    /// 7000 x 7000). The download cap of 20 MB says nothing about this: a
    /// 1.1 MB PNG of 20000 x 20000 decoded to 1.5 GB (security review
    /// 2026-10-24, L12), and anyone can upload a front.
    public const long MaxPixels = 50_000_000;

    /// A JPEG no larger than max pixels on its longer side (smaller images are
    /// returned as they came). Null when the bytes are not an image, or are
    /// one larger than MaxPixels. The size is read from the header before
    /// anything is decoded, and a JPEG is decoded straight at the smallest
    /// size its codec offers that is still at least max (1/2, 1/4, 1/8).
    public static byte[]? ScaleToFit(byte[] image, int max)
    {
        // SKBitmap.Decode throws on bytes no codec recognises (a cut-off
        // download, an error page); asking for the codec first does not.
        using var data = SkiaSharp.SKData.CreateCopy(image);
        using var codec = SkiaSharp.SKCodec.Create(data);
        if (codec is null) return null;
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) return null;
        if (width <= max && height <= max) return image;
        var fit = (float)max / Math.Max(width, height);
        var decodeAt = codec.GetScaledDimensions(fit);
        if (Math.Max(decodeAt.Width, decodeAt.Height) < max) decodeAt = new SkiaSharp.SKSizeI(width, height);
        using var bitmap = new SkiaSharp.SKBitmap(codec.Info.WithSize(decodeAt.Width, decodeAt.Height));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SkiaSharp.SKCodecResult.Success or SkiaSharp.SKCodecResult.IncompleteInput)) return null;
        var scale = (double)max / Math.Max(bitmap.Width, bitmap.Height);
        var info = new SkiaSharp.SKImageInfo(Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)));
        using var scaled = bitmap.Resize(info, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
        if (scaled is null) return null;
        using var picture = SkiaSharp.SKImage.FromBitmap(scaled);
        using var jpeg = picture.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
        return jpeg?.ToArray();
    }

    private static async Task<byte[]?> FetchAsync(HttpClient http, string url, CancellationToken ct, int attempts)
    {
        if (!WebLimits.IsArchiveUrl(url)) return null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.IsSuccessStatusCode) return await WebLimits.ReadCappedAsync(response.Content, WebLimits.MaxImageBytes, ct);
            }
            catch (HttpRequestException) when (attempt < attempts) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < attempts) { }
            if (attempt >= attempts) return null;
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }
    }
}
