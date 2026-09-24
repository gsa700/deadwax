using System.Text;
using System.Text.Json;

namespace Deadwax.Metadata;

public sealed record Tag(string Key, string Value);

/// Vorbis comments for one track, built the way whipper built them: everything
/// from one MusicBrainz release lookup, except the ISRC, which comes from the
/// disc itself (the recording's MusicBrainz ISRCs are often other issues'; 52nd
/// Street's disc says USSM11100749, MusicBrainz lists USSM17800265).
///
/// This is the plain, one-release form. Box discs filed as their own album
/// (spec §7) are a layer on top.
public static class ReleaseTags
{
    public static JsonElement Medium(JsonElement release, string? discId, int? fallbackPosition = null)
    {
        foreach (var m in release.GetProperty("media").EnumerateArray())
            if (discId is not null && m.TryGetProperty("discs", out var discs) &&
                discs.EnumerateArray().Any(d => d.GetProperty("id").GetString() == discId))
                return m;
        if (fallbackPosition is { } p)
            foreach (var m in release.GetProperty("media").EnumerateArray())
                if (m.GetProperty("position").GetInt32() == p) return m;
        throw new MusicBrainzException($"Disc {discId} is not on release {release.GetProperty("id").GetString()}.");
    }

    public static IReadOnlyList<Tag> ForTrack(JsonElement release, JsonElement medium, int trackPosition, string? discId, string? isrc)
    {
        var track = medium.GetProperty("tracks").EnumerateArray()
            .First(t => t.GetProperty("position").GetInt32() == trackPosition);
        var recording = track.GetProperty("recording");
        var tags = new List<Tag>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) tags.Add(new Tag(key, value));
        }

        Add("ALBUM", release.GetProperty("title").GetString());
        Add("ALBUMARTIST", Credit(release));
        Add("ARTIST", Credit(track));
        Add("TITLE", track.GetProperty("title").GetString());
        Add("TRACKNUMBER", trackPosition.ToString());
        Add("TRACKTOTAL", medium.GetProperty("track-count").GetInt32().ToString());
        Add("DISCNUMBER", medium.GetProperty("position").GetInt32().ToString());
        Add("DISCTOTAL", release.GetProperty("media").GetArrayLength().ToString());
        Add("DATE", Str(release, "date"));      // the full date, as whipper wrote it: "2003-02-18"
        Add("ISRC", isrc);

        Add("MUSICBRAINZ_DISCID", discId);
        Add("MUSICBRAINZ_ALBUMID", Str(release, "id"));
        Add("MUSICBRAINZ_RELEASEGROUPID", release.TryGetProperty("release-group", out var rg) ? Str(rg, "id") : null);
        Add("MUSICBRAINZ_RELEASETRACKID", Str(track, "id"));
        Add("MUSICBRAINZ_TRACKID", Str(recording, "id"));
        foreach (var id in CreditIds(release)) Add("MUSICBRAINZ_ALBUMARTISTID", id);
        foreach (var id in CreditIds(track)) Add("MUSICBRAINZ_ARTISTID", id);

        // A recording can link to the same work twice (a plain and a "live"
        // performance link on MTV Unplugged); whipper wrote each work once.
        var works = Relations(recording, "work").Where(r => Str(r, "type") == "performance")
            .Select(r => r.GetProperty("work")).DistinctBy(w => Str(w, "id")).ToList();
        foreach (var w in works) Add("MUSICBRAINZ_WORKID", Str(w, "id"));
        foreach (var name in works.SelectMany(w => Relations(w, "artist"))
                     .Where(r => Str(r, "type") == "composer").Select(ArtistName).Distinct())
            Add("COMPOSER", name);

        // Everyone who played or sang on the recording, once each, sorted. A plain
        // "performer" credit counts too; it is how a band itself is credited
        // (Supertramp on "It's Raining Again").
        foreach (var name in Relations(recording, "artist")
                     .Where(r => Str(r, "type") is "instrument" or "vocal" or "performer")
                     .Select(ArtistName).Distinct().Order(StringComparer.Ordinal))
            Add("PERFORMER", name);

        return tags;
    }

    /// An artist credit as printed: each name followed by its join phrase
    /// ("Crosby, Stills, Nash & Young" is one artist; "Elton John & Kiki Dee"
    /// is two credits joined by " & ").
    private static string? Credit(JsonElement owner)
    {
        if (!owner.TryGetProperty("artist-credit", out var credit)) return null;
        var sb = new StringBuilder();
        foreach (var c in credit.EnumerateArray()) sb.Append(Str(c, "name")).Append(Str(c, "joinphrase"));
        return sb.ToString();
    }

    private static IEnumerable<string> CreditIds(JsonElement owner) =>
        owner.TryGetProperty("artist-credit", out var credit)
            ? credit.EnumerateArray().Select(c => Str(c.GetProperty("artist"), "id")!).Where(s => s is not null)
            : [];

    private static IEnumerable<JsonElement> Relations(JsonElement owner, string targetType) =>
        owner.TryGetProperty("relations", out var rels)
            ? rels.EnumerateArray().Where(r => Str(r, "target-type") == targetType)
            : [];

    private static string ArtistName(JsonElement relation) => Str(relation.GetProperty("artist"), "name")!;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
