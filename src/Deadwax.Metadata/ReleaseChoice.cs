using System.Text.Json;

namespace Deadwax.Metadata;

/// One MusicBrainz release a disc ID is attached to, as the Disc screen lists it.
public sealed record ReleaseCandidate(
    string Id, string Title, string? Date, string? Country, int MediaCount, int? DiscPosition, string? MediumTitle)
{
    public override string ToString() =>
        $"{Title}{(MediaCount > 1 ? $" (disc {DiscPosition} of {MediaCount})" : "")} · {Date ?? "no date"} · {Country ?? "?"} · {Id}";
}

public static class ReleaseChoice
{
    /// The releases in a /discid/ answer, with where this disc sits in each.
    public static IReadOnlyList<ReleaseCandidate> FromDiscLookup(JsonElement disc, string discId)
    {
        if (!disc.TryGetProperty("releases", out var releases)) return [];
        var list = new List<ReleaseCandidate>();
        foreach (var r in releases.EnumerateArray())
        {
            int? position = null;
            string? mediumTitle = null;
            var media = r.TryGetProperty("media", out var m) ? m : default;
            var mediaCount = media.ValueKind == JsonValueKind.Array ? media.GetArrayLength() : 1;
            if (media.ValueKind == JsonValueKind.Array)
                foreach (var medium in media.EnumerateArray())
                    if (medium.TryGetProperty("discs", out var discs) &&
                        discs.EnumerateArray().Any(d => d.GetProperty("id").GetString() == discId))
                    {
                        position = medium.GetProperty("position").GetInt32();
                        mediumTitle = medium.TryGetProperty("title", out var t) ? t.GetString() : null;
                    }
            list.Add(new ReleaseCandidate(
                r.GetProperty("id").GetString()!, r.GetProperty("title").GetString() ?? "",
                Str(r, "date"), Str(r, "country"), mediaCount, position, string.IsNullOrEmpty(mediumTitle) ? null : mediumTitle));
        }
        return list;
    }

    /// The album folder's title in whipper's disc template: a multi-disc
    /// release's disc is "Title (Disc N of M): Medium title", which is how every
    /// box disc in the library arrived before music-unbox refiled it.
    public static string DiscTitle(JsonElement release, JsonElement medium)
    {
        var title = release.GetProperty("title").GetString() ?? "";
        var count = release.GetProperty("media").GetArrayLength();
        if (count <= 1) return title;
        var position = medium.GetProperty("position").GetInt32();
        var mediumTitle = medium.TryGetProperty("title", out var t) ? t.GetString() : null;
        return $"{title} (Disc {position} of {count})" + (string.IsNullOrEmpty(mediumTitle) ? "" : $": {mediumTitle}");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
