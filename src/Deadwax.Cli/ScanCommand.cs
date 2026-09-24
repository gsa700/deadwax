using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Verify;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// Reads the disc in the drive and prints what Deadwax knows about it. With
/// --against, this is the drive half of gate G1: the TOC and every ID must equal
/// what whipper logged when it ripped the same disc.
internal static class ScanCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var device = options.Value("--device") ?? CdDrive.DefaultDevice;
        var against = options.Value("--against");
        var readIsrc = options.Flag("--isrc");
        var online = options.Flag("--online");
        options.Rest();

        WhipperLog? log = null;
        string? cuePath = null;
        if (against is not null)
        {
            (log, cuePath) = FindLog(Home(against));
            if (log is null) return Fail($"no whipper .log found at {against}");
        }

        using var drive = CdDrive.Open(device);
        var identity = drive.ReadIdentity();
        var toc = drive.ReadToc();
        var catalog = drive.ReadCatalog();
        var isrcs = readIsrc
            ? toc.Tracks.Where(t => t.IsAudio).ToDictionary(t => t.Number, t => drive.ReadIsrc(t.Number))
            : null;

        var mbId = DiscIds.MusicBrainz(toc);
        var cddb = DiscIds.Cddb(toc);
        var ar = AccurateRipId.From(toc, cddb);

        Console.WriteLine($"Drive        {identity?.ToString() ?? "(unknown)"} at {device}");
        Console.WriteLine($"Tracks       {toc.FirstTrack}-{toc.LastTrack}, {toc.Tracks.Count(t => t.IsAudio)} audio, lead-out at sector {toc.LeadoutLsn} ({Clock(toc.LeadoutLsn)})");
        Console.WriteLine($"Catalog      {catalog ?? "(none)"}");
        Console.WriteLine();
        Console.WriteLine("  #   start sector  length     ISRC");
        foreach (var t in toc.Tracks)
        {
            var isrc = isrcs is not null && isrcs.TryGetValue(t.Number, out var i) ? i ?? "-" : "";
            Console.WriteLine($"  {t.Number,2}  {t.StartLsn,12}  {Clock(toc.EndLsn(t) - t.StartLsn),-9}  {(t.IsAudio ? isrc : "(data)")}");
        }
        Console.WriteLine();
        Console.WriteLine($"MusicBrainz  {mbId}");
        Console.WriteLine($"             {DiscIds.MusicBrainzAttachUrl(toc)}");
        Console.WriteLine($"CDDB         {Hex(cddb).ToLowerInvariant()}");
        Console.WriteLine($"AccurateRip  {ar.Url}");

        var failures = 0;
        if (log is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"Against {log.Path}");
            failures += Compare(log, toc, mbId, cddb);
            if (cuePath is not null) failures += CompareCue(cuePath, catalog, isrcs);
        }

        if (online)
        {
            Console.WriteLine();
            using var http = NewHttp();
            if (log is not null)
            {
                failures += await CheckLogsCommand.CheckAccurateRipAsync(http, log, ar) ? 0 : 1;
            }
            else
            {
                var blocks = await AccurateRipDatabase.FetchAsync(http, ar);
                Console.WriteLine(blocks is null
                    ? "AccurateRip: this disc is not in the database."
                    : $"AccurateRip: found, {blocks.Count} pressing(s), best confidence on track 1: {blocks.Max(b => b.Tracks.Count > 0 ? b.Tracks[0].Confidence : 0)}");
            }
        }

        if (log is not null)
        {
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "G1: PASS, everything matches whipper's log." : $"G1: FAIL, {failures} difference(s).");
        }
        return failures == 0 ? 0 : 1;
    }

    private static int Compare(WhipperLog log, Toc toc, string mbId, uint cddb)
    {
        var failures = 0;
        void Check(string what, string ours, string? theirs)
        {
            var same = string.Equals(ours, theirs, StringComparison.OrdinalIgnoreCase);
            if (!same) failures++;
            Console.WriteLine($"  {(same ? "same" : "DIFF")}  {what,-16} {ours}{(same ? "" : $"  (whipper: {theirs ?? "none"})")}");
        }

        var logged = log.ToToc();
        Check("track count", toc.Tracks.Count.ToString(), logged.Tracks.Count.ToString());
        foreach (var t in toc.Tracks)
        {
            var theirs = logged.Tracks.FirstOrDefault(x => x.Number == t.Number);
            Check($"track {t.Number} start", t.StartLsn.ToString(), theirs?.StartLsn.ToString());
        }
        Check("lead-out", toc.LeadoutLsn.ToString(), logged.LeadoutLsn.ToString());
        Check("MusicBrainz TOC", DiscIds.MusicBrainzToc(toc), log.MusicBrainzToc);
        Check("MusicBrainz ID", mbId, log.MusicBrainzId);
        Check("CDDB ID", Hex(cddb), log.CddbId is { } c ? Hex(c) : null);
        return failures;
    }

    /// Catalog and ISRCs are in whipper's .cue, not its log. whipper writes the
    /// catalog two ways ("CATALOG n" and an indented "UPC_EAN n"), and writes an
    /// all-zero catalog when the disc has none.
    private static int CompareCue(string cuePath, string? catalog, Dictionary<int, string?>? isrcs)
    {
        var failures = 0;
        var lines = File.ReadAllLines(cuePath);
        var cueCatalog = lines.Select(l => l.Trim())
            .Where(l => l.StartsWith("CATALOG ", StringComparison.Ordinal) || l.StartsWith("UPC_EAN ", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(' ') + 1)..].Trim())
            .FirstOrDefault(v => v.Any(c => c != '0'));
        var same = catalog == cueCatalog;
        if (!same) failures++;
        Console.WriteLine($"  {(same ? "same" : "DIFF")}  {"catalog",-16} {catalog ?? "none"}{(same ? "" : $"  (whipper: {cueCatalog ?? "none"})")}");

        if (isrcs is null) return failures;
        var cueIsrcs = new Dictionary<int, string>();
        var track = 0;
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.StartsWith("TRACK ", StringComparison.Ordinal)) track = int.Parse(l.Split(' ')[1]);
            else if (l.StartsWith("ISRC ", StringComparison.Ordinal) && track > 0) cueIsrcs.TryAdd(track, l[5..].Trim().Trim('"'));
        }
        foreach (var (n, ours) in isrcs)
        {
            cueIsrcs.TryGetValue(n, out var theirs);
            var s = ours == theirs;
            if (!s) failures++;
            Console.WriteLine($"  {(s ? "same" : "DIFF")}  {$"track {n} ISRC",-16} {ours ?? "none"}{(s ? "" : $"  (whipper: {theirs ?? "none"})")}");
        }
        return failures;
    }

    private static (WhipperLog? Log, string? Cue) FindLog(string path)
    {
        string? logPath = File.Exists(path) ? path
            : Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.log").FirstOrDefault()
            : null;
        if (logPath is null) return (null, null);
        var dir = Path.GetDirectoryName(logPath)!;
        var cue = Directory.EnumerateFiles(dir, "*.cue").FirstOrDefault();
        return (WhipperLog.Load(logPath), cue);
    }

    private static string Clock(int sectors)
    {
        var seconds = sectors / Toc.SectorsPerSecond;
        return $"{seconds / 60}:{seconds % 60:D2}.{sectors % Toc.SectorsPerSecond:D2}";
    }
}
