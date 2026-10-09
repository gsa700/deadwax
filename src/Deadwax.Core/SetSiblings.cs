using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.Core;

/// Other discs of the same set already in the library, ripped from a DIFFERENT
/// edition of it. Players group a set's discs by their MusicBrainz release id
/// (MUSICBRAINZ_ALBUMID); two discs of one set with two ids show as two
/// albums. music-audit's cross-disc check found the Toto box this way
/// (2026-09-17); Deadwax asks before the rip instead, so it never happens.
///
/// Matched by release GROUP: the same set in another edition shares it. A disc
/// filed as its own album (Unbox) carries that album's group, not the set's,
/// so it is never counted here.
public static class SetSiblings
{
    public sealed record Sibling(string Folder, string ReleaseId, string? Disc);

    public static IReadOnlyList<Sibling> Find(string library, ReleasePlan plan)
    {
        if (plan.ReleaseGroupId is null || plan.ReleaseId is null || plan.MediaCount < 2) return [];

        // Where the set's other discs would be: the artist's folder, or for
        // the one-level style the library itself, by the "Artist - " prefix.
        var flat = Filing.Current.Folder == FolderStyle.ArtistDashAlbum;
        var parent = flat ? library : Path.Combine(library, FileNames.ArtistFolder(plan.AlbumArtist));
        if (!Directory.Exists(parent)) return [];
        var prefix = FileNames.Safe(plan.AlbumArtist) + " - ";

        var found = new List<Sibling>();
        foreach (var dir in Directory.EnumerateDirectories(parent).Order(StringComparer.Ordinal))
        {
            if (flat && !Path.GetFileName(dir).StartsWith(prefix, StringComparison.Ordinal)) continue;
            var flac = Directory.EnumerateFiles(dir, "*.flac").Order(StringComparer.Ordinal).FirstOrDefault();
            if (flac is null) continue;
            IReadOnlyList<KeyValuePair<string, string>> tags;
            try { tags = FlacTags.Read(flac); } catch { continue; }
            string? Tag(string key) => tags.FirstOrDefault(t => t.Key == key).Value;

            if (Tag("MUSICBRAINZ_RELEASEGROUPID") != plan.ReleaseGroupId) continue;
            if (!int.TryParse(Tag("DISCTOTAL"), out var total) || total < 2) continue;
            if (Tag("MUSICBRAINZ_ALBUMID") is not { } release || release == plan.ReleaseId) continue;
            found.Add(new Sibling(Path.GetFileName(dir), release, Tag("DISCNUMBER")));
        }
        return found;
    }
}
