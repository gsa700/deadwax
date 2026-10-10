using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.Core;

/// A disc of a set, refiled as the album it originally was. Ported from his
/// music-unbox (2026-10-07), which Deadwax used to call after a rip.
///
/// "Original Album Classics", "The Studio Albums 1972–1979" and the like are
/// complete albums in one sleeve. Ripped as sold, every disc carries the
/// collection's name and the year it was issued, and a player that groups by
/// album shows one tile wearing the slipcase. Filed as its album, a disc gets:
///
///   ALBUM       the album's own title (the disc's subtitle)
///   DATE        the year of the album's first release
///   DISCNUMBER  1, DISCTOTAL 1
///   MUSICBRAINZ_RELEASEGROUPID  the album
///   MUSICBRAINZ_ALBUMID         removed: it named the box, and the back cover
///                               would be the box's. No single release of the
///                               album is known, so the album is named and not
///                               a release. The disc's own fingerprint stays
///                               in MUSICBRAINZ_DISCID.
///   cover.jpg   the album's front, replacing the slipcase
///   back.jpg    deleted (it was the box's); the back cover step refetches it
///   the folder  renamed "YYYY - Title"
///
/// Whether a set is split at all is his choice, per set, on the Disc screen.
/// Under that choice is a second net: the subtitle must resolve in MusicBrainz
/// as an album or EP by that artist. "Dawn to Dusk", "Companion Disc" or
/// "1996–2000" do not, and such a disc is left as it is.
///
/// Old tags and the replaced art are copied to BackupRoot before anything
/// changes, and nothing is overwritten: a folder that already exists stops it.
public static partial class Unbox
{
    public sealed record Plan(string Artist, string Title, string Year, string ReleaseGroupId, string Target);

    /// Plan is set when the disc can be split; otherwise Reason says why not.
    public sealed record Decision(Plan? Plan, string? Reason);

    public sealed record Result(string AlbumDirectory, bool FrontReplaced);

    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "deadwax", "unbox-backup");

    public static bool IsSetDisc(string albumDir) => Folder().IsMatch(Path.GetFileName(albumDir));

    /// The disc's album title from its folder name, as Deadwax and whipper
    /// write it: "2013 - The Studio Albums 1972–1979 (Disc 2 of 6): Desperado".
    /// Null when the folder is not a disc of a set or names no title.
    public static string? Subtitle(string folder)
    {
        var m = Folder().Match(folder);
        if (!m.Success) return null;
        // Mötley Crüe's discs carry the album's year: "Dr. Feelgood (1989)".
        var title = TrailingYear().Replace(m.Groups["title"].Value, "").Trim();
        return title.Length == 0 ? null : title;
    }

    /// What it would do with this disc, asking MusicBrainz; writes nothing.
    public static async Task<Decision> PlanAsync(string albumDir, MusicBrainzClient mb, CancellationToken ct = default)
    {
        var folder = Path.GetFileName(albumDir);
        var flac = FirstFlac(albumDir);
        if (flac is null) return new(null, "no FLAC files");
        var title = Subtitle(folder);
        if (title is null) return new(null, "no album title in the folder name");
        if (NotAnAlbum.Contains(Norm(title))) return new(null, $"\"{title}\" is not an album title");

        var tags = FlacTags.Read(flac);
        var album = tags.FirstOrDefault(t => t.Key == "ALBUM").Value;
        if (Norm(album) == Norm(title)) return new(null, "already tagged as its own album");
        var artist = tags.FirstOrDefault(t => t.Key == "ALBUMARTIST").Value
                     ?? Path.GetFileName(Path.GetDirectoryName(albumDir)!);

        // A title's own quotes are typography; they are dropped rather than
        // escaped, and the hit is matched against the title afterwards anyway.
        var query = $"artist:\"{artist.Replace("\"", "")}\" AND releasegroup:\"{title.Replace("\"", "")}\" AND (primarytype:Album OR primarytype:EP)";
        JsonDocument? found;
        try { found = await mb.SearchReleaseGroupsAsync(query, 5, ct); }
        catch (Exception e) when (e is HttpRequestException or MusicBrainzException or JsonException
                                    || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new(null, $"MusicBrainz could not be asked ({e.Message})");
        }
        (string Id, string Year)? hit;
        using (found) hit = found is null ? null : Pick(found.RootElement, title);
        if (hit is not { } h) return new(null, $"\"{title}\" does not resolve as an album or EP by {artist}");

        var target = Path.Combine(Path.GetDirectoryName(albumDir)!, FileNames.AlbumLeaf(Filing.Current.Folder, artist, h.Year, title));
        if (Directory.Exists(target) && Path.GetFullPath(target) != Path.GetFullPath(albumDir))
            return new(null, $"{Path.GetFileName(target)} already exists");
        return new(new Plan(artist, title, h.Year, h.Id, target), null);
    }

    /// The release group a search answered for this title: an album or EP
    /// with this exact title (ignoring case and punctuation), scored 90 or
    /// more, not a compilation, interview, remix or DJ mix. A plain studio
    /// album beats a live one, then the earliest: taking the first hit picked
    /// a 1996 live "Eagles" and a 1992 live "Hotel California" over the 1972
    /// and 1976 records. A live album still wins when it is the only match
    /// (REO's "Live: You Get What You Play For").
    public static (string Id, string Year)? Pick(JsonElement search, string title)
    {
        if (!search.TryGetProperty("release-groups", out var groups) || groups.ValueKind != JsonValueKind.Array) return null;
        var matches = new List<(bool Secondary, string Year, string Id)>();
        foreach (var g in groups.EnumerateArray())
        {
            var secondary = g.TryGetProperty("secondary-types", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(x => (x.GetString() ?? "").ToLowerInvariant()).ToHashSet() : [];
            if (secondary.Overlaps(["compilation", "interview", "remix", "dj-mix"])) continue;
            if (Norm(Str(g, "title")) != Norm(title)) continue;
            var score = g.TryGetProperty("score", out var sc) ? sc.ValueKind == JsonValueKind.Number ? sc.GetInt32() : int.TryParse(sc.GetString(), out var n) ? n : 0 : 0;
            if (score < 90) continue;
            var date = Str(g, "first-release-date");
            if (date is not { Length: >= 4 } || !date[..4].All(char.IsAsciiDigit)) continue;
            if (Str(g, "id") is not { } id) continue;
            matches.Add((secondary.Count > 0, date[..4], id));
        }
        if (matches.Count == 0) return null;
        var best = matches.OrderBy(m => m.Secondary).ThenBy(m => m.Year, StringComparer.Ordinal).ThenBy(m => m.Id, StringComparer.Ordinal).First();
        return (best.Id, best.Year);
    }

    /// Retags the disc, replaces its front, drops its back and renames the
    /// folder, after copying the old tags and art to BackupRoot.
    public static async Task<Result> ApplyAsync(string albumDir, Plan plan, Action<string> say, CancellationToken ct = default)
    {
        if (Directory.Exists(plan.Target) && Path.GetFullPath(plan.Target) != Path.GetFullPath(albumDir))
            throw new RipException($"{plan.Target} already exists; nothing changed.");
        var flacs = Directory.EnumerateFiles(albumDir, "*.flac").Order(StringComparer.Ordinal).ToList();

        var keep = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            Path.GetFileName(Path.GetDirectoryName(albumDir)!), Path.GetFileName(albumDir));
        Directory.CreateDirectory(keep);
        foreach (var flac in flacs)
        {
            var lines = FlacTags.Read(flac).Select(t => $"{t.Key}={t.Value}\n");
            await File.WriteAllTextAsync(Path.Combine(keep, Path.GetFileName(flac) + ".tags"), string.Concat(lines), ct);
        }
        foreach (var art in new[] { "cover.jpg", "back.jpg" })
            if (File.Exists(Path.Combine(albumDir, art))) File.Copy(Path.Combine(albumDir, art), Path.Combine(keep, art), overwrite: true);
        say($"  old tags and art kept in {keep}");

        var set = new Dictionary<string, string>
        {
            ["ALBUM"] = plan.Title,
            ["DATE"] = plan.Year,
            ["DISCNUMBER"] = "1",
            ["DISCTOTAL"] = "1",
            ["MUSICBRAINZ_RELEASEGROUPID"] = plan.ReleaseGroupId,
        };
        foreach (var flac in flacs) FlacTags.Update(flac, set, ["MUSICBRAINZ_ALBUMID"]);

        byte[]? front = null;
        try
        {
            using var http = RipSession.NewHttp(30);
            front = await CoverArt.GroupFrontAsync(http, plan.ReleaseGroupId, ct);
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
        var replaced = front is not null && BackCover.LooksLikeAnImage(front);
        if (replaced)
        {
            var cover = Path.Combine(albumDir, "cover.jpg");
            await File.WriteAllBytesAsync(cover + ".part", front!, ct);
            File.Move(cover + ".part", cover, overwrite: true);
        }
        File.Delete(Path.Combine(albumDir, "back.jpg"));

        if (Path.GetFullPath(plan.Target) != Path.GetFullPath(albumDir))
        {
            // The target's name came from MusicBrainz: it must stay beside the album it renames.
            if (!FileNames.IsInside(Path.GetDirectoryName(albumDir)!, plan.Target))
                throw new IOException($"{plan.Target} is not beside {albumDir}; not moving it.");
            Directory.Move(albumDir, plan.Target);
        }
        return new Result(plan.Target, replaced);
    }

    /// Titles that are never an album, even when a disc carries one.
    private static readonly HashSet<string> NotAnAlbum = new[]
    {
        "original album", "the original album", "companion disc", "the companion disc",
        "live bonus tracks", "bonus tracks", "bonus disc", "extras",
    }.Select(Norm).ToHashSet();

    /// Lower case, accents dropped, letters and digits only.
    public static string Norm(string? s)
    {
        var sb = new StringBuilder();
        foreach (var c in (s ?? "").Normalize(NormalizationForm.FormKD).ToLowerInvariant())
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static string? FirstFlac(string dir) =>
        Directory.EnumerateFiles(dir, "*.flac").Order(StringComparer.Ordinal).FirstOrDefault();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // The colon is optional: one Led Zeppelin folder has none.
    // The year in front is optional: only the default folder style puts it
    // there ("Album (1989)" ends with it, and TrailingYear takes that off the
    // title; "Artist - Album" begins with the artist, which the lazy
    // collection group absorbs).
    [GeneratedRegex(@"^(?:\d{4} - )?(?<collection>.+?) \(Disc (?<n>\d+) of (?<of>\d+)\)(?:\s*:\s*|\s+)?(?<title>.*)$")]
    private static partial Regex Folder();

    [GeneratedRegex(@"\s*\((?:19|20)\d{2}\)\s*$")]
    private static partial Regex TrailingYear();
}
