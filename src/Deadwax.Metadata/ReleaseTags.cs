using System.Text;
using System.Text.Json;
using Deadwax.Drive;

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
        if (FindMedium(release, discId) is { } attached) return attached;
        if (fallbackPosition is { } p)
            foreach (var m in release.GetProperty("media").EnumerateArray())
                if (m.GetProperty("position").GetInt32() == p) return m;
        throw new MusicBrainzException($"Disc {discId} is not on release {release.GetProperty("id").GetString()}.");
    }

    /// The medium this disc ID is attached to, or null.
    public static JsonElement? FindMedium(JsonElement release, string? discId)
    {
        if (discId is null) return null;
        foreach (var m in release.GetProperty("media").EnumerateArray())
            if (m.TryGetProperty("discs", out var discs) &&
                discs.EnumerateArray().Any(d => d.GetProperty("id").GetString() == discId))
                return m;
        return null;
    }

    /// Tolerances for MusicBrainz's track lengths against the TOC. Most agree
    /// to well under a second, but not all: Back in Black's track 10 is 4:26
    /// on the disc and 4:12 on its own, correctly attached release. So one
    /// track may be off by up to LengthOutlierSeconds; the rest must be within
    /// LengthToleranceSeconds.
    public const int LengthToleranceSeconds = 3;
    public const int LengthOutlierSeconds = 30;

    /// The medium of a release the disc ID was never attached to, found from
    /// the disc itself: the same number of audio tracks, and lengths that
    /// agree with MusicBrainz's (see LengthToleranceSeconds). A remaster sold alone
    /// and in box sets often has its disc ID on the boxes only (Coda, 2015
    /// single CD, 2026-09-29), so the edition in his hands never comes up.
    public static JsonElement MediumByTracks(JsonElement release, Toc toc)
    {
        // The last track ends where disc IDs end the audio: on an enhanced CD
        // that is before the gap to the data session, not at the data track.
        var ids = toc.IdTracks;
        var disc = ids.Where(t => t.IsAudio)
            .Select(t => (t.Number, Seconds: ((t == ids[^1] ? toc.AudioLeadoutLsn : toc.EndLsn(t)) - t.StartLsn) / (double)Toc.SectorsPerSecond))
            .ToList();
        var media = release.GetProperty("media").EnumerateArray().ToList();
        var sameCount = media.Where(m => m.GetProperty("tracks").GetArrayLength() == disc.Count).ToList();
        if (sameCount.Count == 0)
            throw new MusicBrainzException(
                $"This release has no disc with {disc.Count} tracks (its discs have {string.Join(", ", media.Select(m => m.GetProperty("tracks").GetArrayLength()))}).");

        string? firstProblem = null;
        foreach (var m in sameCount)
        {
            var tracks = m.GetProperty("tracks").EnumerateArray().ToList();
            var problem = LengthProblem(tracks, disc);
            if (problem is null) return m;
            firstProblem ??= problem;
        }
        throw new MusicBrainzException(firstProblem!);
    }

    private static string? LengthProblem(List<JsonElement> tracks, List<(int Number, double Seconds)> disc)
    {
        var checkedAny = false;
        string? outlier = null;
        for (var i = 0; i < disc.Count; i++)
        {
            if (TrackMilliseconds(tracks[i]) is not { } ms) continue;
            checkedAny = true;
            var off = Math.Abs(ms / 1000.0 - disc[i].Seconds);
            if (off <= LengthToleranceSeconds) continue;
            var problem = $"Track {disc[i].Number} is {Clock(disc[i].Seconds)} on the disc but {Clock(ms / 1000.0)} on this release.";
            if (off > LengthOutlierSeconds || outlier is not null) return outlier ?? problem;
            outlier = problem;
        }
        return checkedAny ? null : "This release has no track lengths to check the disc against.";
    }

    private static int? TrackMilliseconds(JsonElement track)
    {
        if (track.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number) return l.GetInt32();
        if (track.TryGetProperty("recording", out var r) && r.TryGetProperty("length", out var rl) && rl.ValueKind == JsonValueKind.Number)
            return rl.GetInt32();
        return null;
    }

    private static string Clock(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:D2}";

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
