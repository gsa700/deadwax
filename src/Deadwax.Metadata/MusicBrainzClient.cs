using System.Net;
using System.Text.Json;

namespace Deadwax.Metadata;

public sealed class MusicBrainzException(string message) : Exception(message);

/// The MusicBrainz web service, used the way it asks to be used: a real
/// User-Agent, at most one request a second, and 503 ("slow down") retried
/// with a growing pause. Only a 404 means "no such thing"; the wizard once
/// reported a busy server as "disc not attached" and sent him off doing
/// pointless submissions.
public sealed class MusicBrainzClient : IDisposable
{
    public const string ReleaseIncludes =
        "recordings+artist-credits+labels+isrcs+release-groups+discids+recording-level-rels+work-rels+work-level-rels+artist-rels";

    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(1100);

    private readonly HttpClient _http;
    private readonly string? _cacheDir;
    private DateTime _last = DateTime.MinValue;

    /// cacheDir: keep release JSON on disk (a release does not change between
    /// the lookup at pick time and the tagging after the rip); null for none.
    public MusicBrainzClient(string? cacheDir = null)
    {
        _http = new HttpClient { BaseAddress = new Uri("https://musicbrainz.org/ws/2/"), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Deadwax/0.1 ( https://github.com/gsa700/deadwax )");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _cacheDir = cacheDir;
        if (cacheDir is not null) Directory.CreateDirectory(cacheDir);
    }

    public static string DefaultCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "deadwax", "musicbrainz");

    /// A release with everything tagging needs, or null if MusicBrainz has no
    /// such release.
    public async Task<JsonDocument?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
    {
        var cached = _cacheDir is null ? null : Path.Combine(_cacheDir, $"release-{releaseId}.json");
        if (cached is not null && File.Exists(cached))
            return JsonDocument.Parse(await File.ReadAllBytesAsync(cached, ct));

        var body = await GetAsync($"release/{releaseId}?inc={ReleaseIncludes}&fmt=json", ct);
        if (body is null) return null;
        if (cached is not null) await File.WriteAllBytesAsync(cached, body, ct);
        return JsonDocument.Parse(body);
    }

    /// The releases a disc ID is attached to, each with its barcode, and with
    /// its label and catalog number (`inc=labels`). Note `inc=releases` is
    /// invalid on this endpoint (HTTP 400); releases come back by default.
    public async Task<JsonDocument?> GetDiscAsync(string discId, CancellationToken ct = default)
    {
        var body = await GetAsync($"discid/{discId}?inc=labels&fmt=json", ct);
        return body is null ? null : JsonDocument.Parse(body);
    }

    private async Task<byte[]?> GetAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var wait = _last + Pace - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTime.UtcNow;

            using var response = await _http.GetAsync(path, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new MusicBrainzException($"MusicBrainz answered {(int)response.StatusCode} for {path}");
            return await response.Content.ReadAsByteArrayAsync(ct);
        }
    }

    public void Dispose() => _http.Dispose();
}
