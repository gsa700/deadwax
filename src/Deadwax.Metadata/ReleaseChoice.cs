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

    /// The album folder's title, whipper's "%d" exactly (mbngs._getMetadata):
    /// the release title, then its disambiguation comment in brackets (even on
    /// a one-disc release: "Gish (Reissue of 2011 remaster)"), then "(Disc N of
    /// M)" on a multi-disc release, then ": Medium title" when the medium has
    /// one. So "Original Album Classics (Volume 2) (Disc 4 of 5): Storm Front".
    public static string DiscTitle(JsonElement release, JsonElement medium)
    {
        var title = release.GetProperty("title").GetString() ?? "";
        if (Str(release, "disambiguation") is { } comment) title += $" ({comment})";
        var count = release.GetProperty("media").GetArrayLength();
        if (count > 1) title += $" (Disc {medium.GetProperty("position").GetInt32()} of {count})";
        if (Str(medium, "title") is { } mediumTitle) title += $": {mediumTitle}";
        return title;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
