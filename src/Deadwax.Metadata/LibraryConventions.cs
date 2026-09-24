using System.Diagnostics;
using System.Text.Json;

namespace Deadwax.Metadata;

/// His library's rules on top of what MusicBrainz says, from how the library was
/// cleaned by hand before Deadwax existed:
///
/// - DATE is the album's ORIGINAL release year, even on a reissue, unless he
///   gives an edition its own year (the Led Zeppelin deluxe sets keep 2014). The
///   folder's "YEAR - Album" is the authority, so the year is a choice made on
///   the Disc screen, defaulting to the original (spec §7).
/// - ALBUMARTIST, and ARTIST where the track is credited to the same act, use
///   the spelling of the artist's existing folder. MusicBrainz credits the same
///   act differently per pressing: "Daryl Hall and John Oates", "Daryl Hall +
///   John Oates", "Smashing Pumpkins", "Metallica & San Francisco Symphony".
public static class LibraryConventions
{
    /// The year a release is first offered with: its release group's first
    /// release, or the release's own year when MusicBrainz has none.
    public static string? OriginalYear(JsonElement release)
    {
        string? first = null;
        if (release.TryGetProperty("release-group", out var rg) && rg.TryGetProperty("first-release-date", out var f))
            first = f.GetString();
        if (string.IsNullOrEmpty(first) && release.TryGetProperty("date", out var d)) first = d.GetString();
        return first is { Length: >= 4 } ? first[..4] : null;
    }

    /// Applies the chosen year and the folder spelling of the album artist.
    /// DATE keeps whipper's full release date when it already falls in the
    /// chosen year, and becomes the bare year otherwise, as the hand-cleaned
    /// albums have it.
    public static IReadOnlyList<Tag> Apply(IReadOnlyList<Tag> tags, string year, string? folderArtist)
    {
        var releaseCredit = tags.FirstOrDefault(t => t.Key == "ALBUMARTIST")?.Value;
        var result = new List<Tag>(tags.Count);
        foreach (var t in tags)
        {
            result.Add(t.Key switch
            {
                "DATE" => t.Value.StartsWith(year, StringComparison.Ordinal) ? t : t with { Value = year },
                "ALBUMARTIST" when folderArtist is not null => t with { Value = folderArtist },
                "ARTIST" when folderArtist is not null && t.Value == releaseCredit => t with { Value = folderArtist },
                _ => t,
            });
        }
        if (!result.Any(t => t.Key == "DATE")) result.Add(new Tag("DATE", year));
        return result;
    }
}

/// How the library already spells each artist, found by the MusicBrainz artist
/// IDs inside its folders.
public sealed class ArtistFolders
{
    private readonly Dictionary<string, string> _byId = new(StringComparer.Ordinal);

    public string? For(string artistId) => _byId.GetValueOrDefault(artistId);

    /// The first album-artist ID of any FLAC in a folder, and that FLAC's
    /// ALBUMARTIST, give the artist and the spelling. One file per album is enough, and an ID seen in two folders
    /// keeps the first (a folder name is his decision; conflicts are for him).
    public static async Task<ArtistFolders> ScanAsync(string library)
    {
        var folders = new ArtistFolders();
        foreach (var artistDir in Directory.EnumerateDirectories(library).Order(StringComparer.Ordinal))
        {
            foreach (var albumDir in Directory.EnumerateDirectories(artistDir))
            {
                var flac = Directory.EnumerateFiles(albumDir, "*.flac").FirstOrDefault();
                if (flac is null) continue;
                // The spelling is the tag's, not the folder's: a folder name has
                // had "/" replaced ("AC_DC" holds AC/DC).
                var id = await FirstTagAsync(flac, "MUSICBRAINZ_ALBUMARTISTID");
                var name = await FirstTagAsync(flac, "ALBUMARTIST");
                if (id is not null && name is not null) folders._byId.TryAdd(id, name);
            }
        }
        return folders;
    }

    private static async Task<string?> FirstTagAsync(string flac, string key)
    {
        var start = new ProcessStartInfo("metaflac") { RedirectStandardOutput = true };
        start.ArgumentList.Add($"--show-tag={key}");
        start.ArgumentList.Add(flac);
        using var p = Process.Start(start)!;
        var line = (await p.StandardOutput.ReadLineAsync())?.Split('=', 2);
        await p.WaitForExitAsync();
        return line is { Length: 2 } ? line[1] : null;
    }
}
