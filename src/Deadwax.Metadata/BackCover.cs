using System.Net;
using System.Text.Json;

namespace Deadwax.Metadata;

/// The back cover from the Cover Art Archive, as back.jpg. Ported from his
/// music-backart (2026-10-07), which Deadwax used to call after a rip.
///
/// Where it comes from, in order:
///
///  1. His pressing: the release's own image index.
///  2. Another edition of the same album that has a back: same country first,
///     then the nearest year. A sibling's back is not his disc's back, but the
///     back is there to be seen (agreed 2026-09-20), and every one that comes
///     from a sibling says so.
///
/// Read from the index, never the /back-500 shortcut: the shortcut serves
/// whichever image is flagged back, and an editor can flag one image Front
/// AND Back. Neil Diamond 50 is filed that way, so all three discs got a
/// back.jpg identical to the cover while the real tray scan sat beside it.
/// An image typed Front is never a back here.
///
/// The release-group endpoint, which rescues a missing front, answers 400 for
/// a back; the MusicBrainz release browse, which says for each edition whether
/// it has a back, is what finds siblings.
///
/// Failing is not the same as "none": a reset, a timeout or a 5xx is reported
/// as Failed, so the caller says "try again later" rather than "there is no
/// back". The archive's image host drops connections for minutes at a time
/// (2026-10-06), so the search has a time budget and stops after three
/// failures in a row.
public static class BackCover
{
    public enum Outcome { Found, None, Failed }

    /// Source: where the image came from, for the log.
    public sealed record Result(Outcome Outcome, byte[]? Image = null, string? Source = null, bool Sibling = false, string? Error = null);

    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);
    private const int Breaker = 3;
    private const string Size = "500";

    public static async Task<Result> FetchAsync(HttpClient http, MusicBrainzClient mb, string? releaseId, string? releaseGroupId, CancellationToken ct = default)
    {
        if (releaseId is not null)
        {
            var own = await BackOfAsync(http, releaseId, ct);
            if (own.Image is not null) return new Result(Outcome.Found, own.Image, $"his own release {releaseId}");
            if (own.Error is not null) return new Result(Outcome.Failed, Error: $"release: {own.Error}");
        }
        if (releaseGroupId is null) return new Result(Outcome.None);

        JsonDocument? browse;
        try { browse = await mb.BrowseReleasesAsync(releaseGroupId, ct); }
        catch (Exception e) when (e is HttpRequestException or MusicBrainzException or JsonException
                                    || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new Result(Outcome.Failed, Error: $"MusicBrainz: {e.Message}");
        }
        if (browse is null) return new Result(Outcome.None);

        var started = DateTime.UtcNow;
        int errors = 0, streak = 0;
        using (browse)
            foreach (var cand in Ranked(browse.RootElement, releaseId))
            {
                if (DateTime.UtcNow - started > Budget)
                    return new Result(Outcome.Failed, Error: $"gave up after {Budget.TotalSeconds:0} s; archive.org is slow or not answering");
                var r = await BackOfAsync(http, cand.Id, ct);
                if (r.Image is not null)
                    return new Result(Outcome.Found, r.Image, $"sibling edition {cand.Id} ({cand.Country ?? "??"} {cand.Date ?? "????"})", Sibling: true);
                if (r.Error is not null)
                {
                    errors++;
                    if (++streak >= Breaker) return new Result(Outcome.Failed, Error: $"archive.org not answering ({r.Error} x{streak})");
                    continue;
                }
                // An edition that says it has a back and then has none: the
                // archive disagreeing with itself. Try the next one.
                streak = 0;
            }
        return errors > 0
            ? new Result(Outcome.Failed, Error: $"{errors} archive error(s) while looking; verdict withheld")
            : new Result(Outcome.None);
    }

    /// One release's back: (image, null), (null, null) for none, or (null, error).
    private static async Task<(byte[]? Image, string? Error)> BackOfAsync(HttpClient http, string releaseId, CancellationToken ct)
    {
        if (!WebLimits.IsMbid(releaseId)) return (null, "bad id");
        var (index, error) = await GetAsync(http, $"https://coverartarchive.org/release/{releaseId}/", ct);
        if (index is null) return (null, error);
        string? url;
        try
        {
            using var doc = JsonDocument.Parse(index);
            url = PickBack(doc.RootElement);
        }
        catch (JsonException) { return (null, "bad index"); }
        if (url is null) return (null, null);

        // The index names the image's URL; only the archive's own hosts are asked.
        url = url.Replace("http://", "https://");
        if (!WebLimits.IsArchiveUrl(url)) return (null, "not an archive URL");
        var (image, imageError) = await GetAsync(http, url, ct);
        if (image is null) return (null, imageError);
        // A CDN error page saved as back.jpg looks entirely normal in a listing.
        return LooksLikeAnImage(image) ? (image, null) : (null, null);
    }

    /// The URL of the back in a release's image index, or null. Never an image
    /// typed Front; the flagged back before any other image typed Back.
    public static string? PickBack(JsonElement index)
    {
        if (!index.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        JsonElement? pick = null;
        foreach (var image in images.EnumerateArray())
        {
            var types = image.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString()).ToList() : [];
            var front = types.Contains("Front") || Flag(image, "front");
            var back = Flag(image, "back");
            if (front || !(back || types.Contains("Back"))) continue;
            if (back) { pick = image; break; }
            pick ??= image;
        }
        if (pick is not { } p) return null;
        if (p.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { Size, "large" })
                if (thumbs.TryGetProperty(key, out var u) && u.GetString() is { Length: > 0 } s) return s;
        return p.TryGetProperty("image", out var full) ? full.GetString() : null;
    }

    public sealed record Edition(string Id, string? Country, string? Date);

    /// The editions in a release browse that have a back, nearest his first:
    /// same country, then the closest year, then the earliest date.
    public static IReadOnlyList<Edition> Ranked(JsonElement browse, string? mine)
    {
        if (!browse.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return [];
        var all = releases.EnumerateArray().Select(r => (
            Edition: new Edition(Str(r, "id") ?? "", Str(r, "country"), Str(r, "date")),
            HasBack: r.TryGetProperty("cover-art-archive", out var caa) && Flag(caa, "back"))).ToList();
        var me = all.FirstOrDefault(r => r.Edition.Id == mine).Edition;
        var myYear = Year(me?.Date);
        return all
            .Where(r => r.HasBack && r.Edition.Id.Length > 0 && r.Edition.Id != mine)
            .Select(r => r.Edition)
            .OrderBy(e => me?.Country is { } c && e.Country == c ? 0 : 1)
            .ThenBy(e => Year(e.Date) is { } y && myYear is { } m ? Math.Abs(y - m) : 999)
            .ThenBy(e => e.Date ?? "9999", StringComparer.Ordinal)
            .ToList();
    }

    /// JPEG or PNG by its first bytes.
    public static bool LooksLikeAnImage(byte[] b) =>
        b.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
        || b.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

    /// Bytes; (null, null) when the archive has no such thing (404, or the 400
    /// it answers for a question it cannot serve); (null, error) when it failed
    /// to answer. A 5xx or a dropped connection is tried once more.
    private static async Task<(byte[]?, string?)> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        string error = "gave up";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return (null, null);
                if (response.IsSuccessStatusCode)
                    return await WebLimits.ReadCappedAsync(response.Content, WebLimits.MaxImageBytes, ct) is { } body
                        ? (body, null) : (null, "too large");
                error = $"http{(int)response.StatusCode}";
                if ((int)response.StatusCode < 500) return (null, error);
            }
            catch (HttpRequestException e) { error = e.HttpRequestError.ToString(); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { error = "timeout"; }
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return (null, error);
    }

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), out var y) ? y : null;
}
