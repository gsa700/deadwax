using System.Text.Json;

namespace Deadwax.Metadata;

/// One MusicBrainz release a disc ID is attached to, as the Disc screen lists it.
public sealed record ReleaseCandidate(
    string Id, string Title, string? Date, string? Country, int MediaCount, int? DiscPosition, string? MediumTitle,
    string? Barcode = null, string? Label = null, string? CatalogNumber = null, string? Disambiguation = null)
{
    /// Whether the disc's own catalog number (UPC/EAN, from its subchannel or
    /// CD-Text) is this release's barcode. A 12-digit UPC and the same number
    /// as a 13-digit EAN differ only by a leading zero. Box discs usually carry
    /// a number of their own, so no match is not evidence against a release.
    public bool BarcodeMatches(string? discCatalog) =>
        Digits(Barcode) is { Length: > 0 } a && Digits(discCatalog) is { Length: > 0 } b && a == b;

    private static string? Digits(string? s) =>
        s is null ? null : new string(s.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');

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
            string? label = null, catalog = null;
            if (r.TryGetProperty("label-info", out var info) && info.ValueKind == JsonValueKind.Array && info.GetArrayLength() > 0)
            {
                var first = info[0];
                label = first.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.Object ? Str(l, "name") : null;
                catalog = Str(first, "catalog-number");
            }
            list.Add(new ReleaseCandidate(
                r.GetProperty("id").GetString()!, r.GetProperty("title").GetString() ?? "",
                Str(r, "date"), Str(r, "country"), mediaCount, position, string.IsNullOrEmpty(mediumTitle) ? null : mediumTitle,
                Str(r, "barcode"), label, catalog, Str(r, "disambiguation")));
        }
        return list;
    }

    /// The album folder's title in whipper's "%d" layout (mbngs._getMetadata):
    /// the release title, then "(Disc N of M)" on a multi-disc release, then
    /// ": Medium title" when the medium has one.
    ///
    /// whipper also put the release's disambiguation comment in brackets after
    /// the title. That comment is a note for telling releases apart on
    /// MusicBrainz, not part of the name: Bangles' "Everything (DIDP 071240)"
    /// carries a matrix code, and he renamed "Dirt (reordered tracklist)" by
    /// hand. So it is left out unless asked for (his call, 2026-09-24); the
    /// Disc screen offers it.
    public static string DiscTitle(JsonElement release, JsonElement medium, bool withDisambiguation = false)
    {
        var title = release.GetProperty("title").GetString() ?? "";
        if (withDisambiguation && Disambiguation(release) is { } comment) title += $" ({comment})";
        var count = release.GetProperty("media").GetArrayLength();
        if (count > 1) title += $" (Disc {medium.GetProperty("position").GetInt32()} of {count})";
        if (Str(medium, "title") is { } mediumTitle) title += $": {mediumTitle}";
        return title;
    }

    public static string? Disambiguation(JsonElement release) => Str(release, "disambiguation");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
