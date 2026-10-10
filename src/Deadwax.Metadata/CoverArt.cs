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
               ?? (!WebLimits.IsMbid(releaseGroupId) ? null
                   : await FetchAsync(http, $"https://coverartarchive.org/release-group/{releaseGroupId}/front-500", ct, attempts));
    }

    /// The release group's front only: for a box disc refiled as its album,
    /// where no single release of that album is known.
    public static Task<byte[]?> GroupFrontAsync(HttpClient http, string releaseGroupId, CancellationToken ct = default, int attempts = DefaultAttempts) =>
        !WebLimits.IsMbid(releaseGroupId) ? Task.FromResult<byte[]?>(null) : FetchAsync(http, $"https://coverartarchive.org/release-group/{releaseGroupId}/front-500", ct, attempts);

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
