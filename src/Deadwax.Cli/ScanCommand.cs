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
        var speed = options.Value("--speed");
        var against = options.Value("--against");
        var full = options.Flag("--full");
        var online = options.Flag("--online");
        options.Rest();

        WhipperLog? log = null;
        string? cuePath = null;
        if (against is not null)
        {
            (log, cuePath) = FindLog(Home(against));
            if (log is null) return Fail($"no whipper .log found at {against}");
        }

        DriveIdentity? identity;
        Toc toc;
        using (var drive = CdDrive.Open(device))
        {
            if (speed is not null) drive.LimitSpeed(int.Parse(speed));
            identity = drive.ReadIdentity();
            toc = drive.ReadToc();
        }

        CdrdaoToc? cdrdao = null;
        if (full)
        {
            Console.Error.WriteLine("Reading catalog, ISRCs, CD-Text and pregaps with cdrdao (about two minutes)...");
            cdrdao = (await Cdrdao.ReadTocAsync(device)).Toc;
        }

        var mbId = DiscIds.MusicBrainz(toc);
        var cddb = DiscIds.Cddb(toc);
        var ar = AccurateRipId.From(toc, cddb);

        Console.WriteLine($"Drive        {identity?.ToString() ?? "(unknown)"} at {device}");
        Console.WriteLine($"Tracks       {toc.FirstTrack}-{toc.LastTrack}, {toc.Tracks.Count(t => t.IsAudio)} audio, lead-out at sector {toc.LeadoutLsn} ({Clock(toc.LeadoutLsn)})");
        if (cdrdao is not null)
        {
            Console.WriteLine($"Catalog      {CdrdaoToc.Real(cdrdao.Catalog) ?? "(none)"}{(CdrdaoToc.Real(cdrdao.DiscText?.UpcEan) is { } upc ? $", CD-Text {upc}" : "")}");
            if (cdrdao.DiscText is { } text) Console.WriteLine($"CD-Text      {text.Performer} / {text.Title}");
        }
        Console.WriteLine();
        Console.WriteLine(cdrdao is null ? "  #   start sector  length" : "  #   start sector  length     pregap  ISRC");
        foreach (var t in toc.Tracks)
        {
            var line = $"  {t.Number,2}  {t.StartLsn,12}  {Clock(toc.EndLsn(t) - t.StartLsn),-9}";
            if (cdrdao?.Tracks.FirstOrDefault(x => x.Number == t.Number) is { } c)
                line += $"  {Clock(c.PregapSectors),-7} {c.Isrc ?? c.Text?.Isrc ?? "-"}";
            Console.WriteLine(t.IsAudio ? line : line + "  (data)");
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
            var logged = log.ToToc();
            var checks = new List<Check>();
            checks.AddRange(CdrdaoChecks.AgainstToc(toc, logged));
            checks.Add(new("MusicBrainz TOC", DiscIds.MusicBrainzToc(toc), log.MusicBrainzToc));
            checks.Add(new("MusicBrainz ID", mbId, log.MusicBrainzId));
            checks.Add(new("CDDB ID", Hex(cddb), log.CddbId is { } id ? Hex(id) : null));
            if (cdrdao is not null)
            {
                // cdrdao's own TOC must agree with libcdio's, then with the cue.
                checks.AddRange(CdrdaoChecks.AgainstToc(cdrdao.ToToc(), toc).Select(c => c with { What = "cdrdao " + c.What }));
                if (cuePath is not null) checks.AddRange(CdrdaoChecks.AgainstCue(cdrdao, WhipperCue.Load(cuePath)));
            }
            foreach (var c in checks) c.Print();
            failures += checks.Count(c => !c.Same);
            if (cdrdao is null) Console.WriteLine("  (catalog and ISRCs not checked: add --full)");
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
            Console.WriteLine(failures == 0 ? "G1: PASS, everything matches whipper." : $"G1: FAIL, {failures} difference(s).");
        }
        return failures == 0 ? 0 : 1;
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
