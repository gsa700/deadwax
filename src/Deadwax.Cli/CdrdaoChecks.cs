using Deadwax.Drive;
using Deadwax.Verify;

namespace Deadwax.Cli;

internal sealed record Check(string What, string Ours, string? Theirs)
{
    public bool Same => string.Equals(Ours, Theirs, StringComparison.OrdinalIgnoreCase);

    public void Print() =>
        Console.WriteLine($"  {(Same ? "same" : "DIFF")}  {What,-16} {Ours}{(Same ? "" : $"  (whipper: {Theirs ?? "none"})")}");
}

internal static class CdrdaoChecks
{
    /// A cdrdao TOC file against a whipper cue sheet: the catalog number, and
    /// every ISRC the disc carries, from the subchannel and from CD-Text.
    public static List<Check> AgainstCue(CdrdaoToc toc, WhipperCue cue)
    {
        var checks = new List<Check>
        {
            new("catalog", CdrdaoToc.Real(toc.Catalog) ?? "none", cue.Catalog ?? "none"),
            new("CD-Text catalog", CdrdaoToc.Real(toc.DiscText?.UpcEan) ?? "none", cue.CdTextCatalog ?? "none"),
        };
        foreach (var t in toc.Tracks.Where(t => t.IsAudio))
        {
            var ours = new[] { t.Isrc, t.Text?.Isrc }.Where(i => !string.IsNullOrEmpty(i)).Distinct().Order();
            var theirs = cue.IsrcsOf(t.Number).Order();
            checks.Add(new($"track {t.Number} ISRC", Join(ours), Join(theirs)));
        }
        return checks;

        static string Join(IEnumerable<string?> s) => s.Any() ? string.Join(" / ", s) : "none";
    }

    /// The TOC implied by a cdrdao file against another TOC (the drive's, or a log's).
    public static List<Check> AgainstToc(Toc fromCdrdao, Toc other)
    {
        var checks = new List<Check> { new("track count", fromCdrdao.Tracks.Count.ToString(), other.Tracks.Count.ToString()) };
        foreach (var t in fromCdrdao.Tracks)
        {
            var theirs = other.Tracks.FirstOrDefault(x => x.Number == t.Number);
            checks.Add(new($"track {t.Number} start", t.StartLsn.ToString(), theirs?.StartLsn.ToString()));
        }
        checks.Add(new("lead-out", fromCdrdao.LeadoutLsn.ToString(), other.LeadoutLsn.ToString()));
        return checks;
    }
}
