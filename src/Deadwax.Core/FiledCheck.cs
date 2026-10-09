using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.Core;

/// "Filed correctly": the album just ripped, checked against the choices in
/// Preferences > Filing. For everyone, where music-audit (his, and about his
/// whole library's conventions) is for him. Reads only.
///
/// What any fresh rip should be: every track tagged, every file named as the
/// chosen style names it, the playlists that were asked for pointing at files
/// that exist, and the cover embedded where that was asked for. The folder's
/// own name is not checked: a disc of a set kept with its set is named for
/// the set, not for its ALBUM tag, and that is deliberate.
public static class FiledCheck
{
    private static readonly string[] Required = ["TITLE", "ARTIST", "ALBUM", "ALBUMARTIST", "TRACKNUMBER", "DATE"];
    private static readonly string[] Shared = ["ALBUM", "ALBUMARTIST", "DATE", "DISCNUMBER", "TRACKTOTAL", "MUSICBRAINZ_ALBUMID"];

    public static IReadOnlyList<string> Run(string albumDir, Filing filing)
    {
        var issues = new List<string>();
        var flacs = Directory.EnumerateFiles(albumDir, "*.flac").Order(StringComparer.Ordinal).ToList();
        if (flacs.Count == 0) return ["no FLAC files in the folder"];

        // Album-wide: what every track must agree on, and the numbering as a
        // whole. The same rules as music-audit's, so his rips run what ships.
        var seen = new Dictionary<string, HashSet<string>>();
        var numbers = new List<int>();

        foreach (var flac in flacs)
        {
            var name = Path.GetFileName(flac);
            IReadOnlyList<KeyValuePair<string, string>> tags;
            try { tags = FlacTags.Read(flac); }
            catch (Exception e) { issues.Add($"{name}: tags unreadable ({e.Message})"); continue; }

            string? Tag(string key) => tags.FirstOrDefault(t => t.Key == key).Value;
            var missing = Required.Where(k => string.IsNullOrWhiteSpace(Tag(k))).ToList();
            if (missing.Count > 0) issues.Add($"{name}: no {string.Join(", ", missing)}");

            foreach (var key in Shared)
                if (Tag(key) is { Length: > 0 } value)
                    (seen.TryGetValue(key, out var values) ? values : seen[key] = []).Add(value);
            if (int.TryParse(Tag("TRACKNUMBER")?.Split('/')[0], out var n)) numbers.Add(n);

            if (int.TryParse(Tag("TRACKNUMBER")?.Split('/')[0], out var number) && Tag("TITLE") is { } title)
            {
                var expected = FileNames.Track(filing.Track, Tag("ALBUMARTIST") ?? "", number, title);
                if (expected != name) issues.Add($"{name}: expected the name {expected}");
            }

            if (filing.EmbedCover && File.Exists(Path.Combine(albumDir, "cover.jpg")) && !CoverEmbed.HasFront(flac))
                issues.Add($"{name}: no embedded cover");
        }

        foreach (var (key, values) in seen.Where(s => s.Value.Count > 1))
            issues.Add($"the tracks disagree on {key}: {string.Join(" | ", values.Order(StringComparer.Ordinal))}");

        var sorted = numbers.Order().ToList();
        if (sorted.Count > 0 && (sorted.Distinct().Count() != sorted.Count || !sorted.SequenceEqual(Enumerable.Range(1, sorted.Count))))
            issues.Add($"track numbers are {string.Join(",", sorted)}, not 1 to {sorted.Count}");
        if (seen.TryGetValue("TRACKTOTAL", out var totals) && totals.Count == 1
            && int.TryParse(totals.First(), out var total) && total != flacs.Count)
            issues.Add($"TRACKTOTAL is {total} but there are {flacs.Count} tracks");

        if (filing.Cue) Playlist(albumDir, "*.cue", issues, CueFiles);
        if (filing.M3u) Playlist(albumDir, "*.m3u", issues, M3uFiles);
        return issues;
    }

    private static void Playlist(string albumDir, string pattern, List<string> issues, Func<string, IEnumerable<string>> files)
    {
        var path = Directory.EnumerateFiles(albumDir, pattern).FirstOrDefault();
        if (path is null) { issues.Add($"no {pattern[1..]} file"); return; }
        foreach (var file in files(File.ReadAllText(path)))
            if (!File.Exists(Path.Combine(albumDir, file)))
                issues.Add($"{Path.GetFileName(path)} names {file}, which is not there");
    }

    private static IEnumerable<string> CueFiles(string text) =>
        text.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("FILE ", StringComparison.Ordinal))
            .Select(l => l[5..l.LastIndexOf(' ')].Trim().Trim('"'));

    private static IEnumerable<string> M3uFiles(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));
}
