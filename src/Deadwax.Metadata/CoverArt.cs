using System.Net;

namespace Deadwax.Metadata;

/// The front cover from the Cover Art Archive, as whipper's `-C file` saved it:
/// the 500-pixel front, which is what nearly every cover.jpg in the library is.
///
/// The archive is flaky in a particular way: one URL answered 200, 500, 500
/// within seconds (music-unbox, 2026-09-23), and a failed fetch read as "no
/// cover" kept a box's slipcase where the album's front belonged. So every
/// fetch retries with a growing pause, and only a 404 means "none".
public static class CoverArt
{
    public static async Task<byte[]?> FrontAsync(HttpClient http, string releaseId, string? releaseGroupId, CancellationToken ct = default)
    {
        return await FetchAsync(http, $"https://coverartarchive.org/release/{releaseId}/front-500", ct)
               ?? (releaseGroupId is null ? null
                   : await FetchAsync(http, $"https://coverartarchive.org/release-group/{releaseGroupId}/front-500", ct));
    }

    private static async Task<byte[]?> FetchAsync(HttpClient http, string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (HttpRequestException) when (attempt < 5) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 5) { }
            if (attempt >= 5) return null;
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }
    }
}
