namespace Deadwax.Verify;

/// The catalog number and ISRCs in a whipper cue sheet, which is where whipper
/// put them (its log has neither).
///
/// whipper copies both from cdrdao's TOC file, from each of the two places the
/// disc can carry them. The subchannel catalog becomes "CATALOG n"; the CD-Text
/// one an indented "UPC_EAN n", sometimes as the file's very first line. A track
/// with an ISRC in both places gets two ISRC lines, quoted and unquoted. A disc
/// with no catalog can still show thirteen zeros.
public sealed class WhipperCue
{
    /// "CATALOG n", from the subchannel.
    public string? Catalog { get; }

    /// "UPC_EAN n", from CD-Text. Usually the same number when both exist, but
    /// not always: Journey's Frontiers carries 0082876858952 in one and
    /// 886919012927 in the other.
    public string? CdTextCatalog { get; }

    public IReadOnlyDictionary<int, IReadOnlySet<string>> Isrcs { get; }

    private WhipperCue(string? catalog, string? cdTextCatalog, Dictionary<int, IReadOnlySet<string>> isrcs)
    {
        Catalog = catalog;
        CdTextCatalog = cdTextCatalog;
        Isrcs = isrcs;
    }

    public static WhipperCue Load(string path)
    {
        string? catalog = null, cdTextCatalog = null;
        var isrcs = new Dictionary<int, HashSet<string>>();
        var track = 0;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space < 0) continue;
            var key = line[..space];
            var value = line[(space + 1)..].Trim().Trim('"');
            switch (key)
            {
                case "CATALOG" when value.Any(c => c != '0'):
                    catalog = value;
                    break;
                case "UPC_EAN" when value.Any(c => c != '0'):
                    cdTextCatalog = value;
                    break;
                case "TRACK":
                    track = int.Parse(value.Split(' ')[0]);
                    break;
                case "ISRC" when track > 0:
                    if (!isrcs.TryGetValue(track, out var set)) isrcs[track] = set = [];
                    set.Add(value);
                    break;
            }
        }
        return new WhipperCue(catalog, cdTextCatalog, isrcs.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value));
    }

    public IReadOnlySet<string> IsrcsOf(int track) =>
        Isrcs.TryGetValue(track, out var set) ? set : new HashSet<string>();
}
