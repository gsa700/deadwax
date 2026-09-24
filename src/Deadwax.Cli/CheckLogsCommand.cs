using Deadwax.Metadata;
using Deadwax.Verify;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// The offline half of gate G1: for every whipper log in the library, rebuild
/// the TOC it records and check Deadwax derives the same IDs whipper did.
internal static class CheckLogsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var onlineText = options.Value("--online");
        var rest = options.Rest();
        var library = Home(rest.Count > 0 ? rest[0] : "~/Music");
        var online = onlineText is null ? 0 : int.Parse(onlineText);

        if (!Directory.Exists(library)) return Fail($"no such directory: {library}");
        var logs = Directory.EnumerateFiles(library, "*.log", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        if (logs.Count == 0) return Fail($"no .log files under {library}");

        int passed = 0, failed = 0, skipped = 0, gaps = 0;
        int tocPassed = 0, tocFailed = 0, tocMissing = 0;
        var arCandidates = new List<(WhipperLog Log, AccurateRipId Id)>();

        foreach (var path in logs)
        {
            WhipperLog log;
            try { log = WhipperLog.Load(path); }
            catch (Exception e) when (e is FormatException or IOException)
            {
                Console.WriteLine($"SKIP  {Rel(path)}: {e.Message}");
                skipped++;
                continue;
            }
            if (log.MusicBrainzId is null || log.CddbId is null || log.TocEntries.Count == 0)
            {
                Console.WriteLine($"SKIP  {Rel(path)}: not a whipper rip log");
                skipped++;
                continue;
            }

            var toc = log.ToToc();
            if (log.HasPreTrackGap) gaps++;
            var problems = new List<string>();

            var mbToc = DiscIds.MusicBrainzToc(toc);
            if (mbToc != log.MusicBrainzToc) problems.Add($"MusicBrainz TOC {mbToc} != logged {log.MusicBrainzToc}");
            var mbId = DiscIds.MusicBrainz(toc);
            if (mbId != log.MusicBrainzId) problems.Add($"MusicBrainz ID {mbId} != logged {log.MusicBrainzId}");
            var cddb = DiscIds.Cddb(toc);
            if (cddb != log.CddbId) problems.Add($"CDDB ID {Hex(cddb)} != logged {Hex(log.CddbId.Value)}");

            // whipper kept cdrdao's TOC file beside the log. Reading it must give
            // back the same TOC the log records, and the catalog and ISRCs whipper
            // copied from it into the cue.
            var dir = Path.GetDirectoryName(path)!;
            var tocFile = Directory.EnumerateFiles(dir, "*.toc").FirstOrDefault();
            var cueFile = Directory.EnumerateFiles(dir, "*.cue").FirstOrDefault();
            if (tocFile is null || cueFile is null)
            {
                tocMissing++;
            }
            else
            {
                List<Check> checks;
                try
                {
                    var cdrdao = Drive.CdrdaoToc.Parse(File.ReadAllText(tocFile));
                    checks = [.. CdrdaoChecks.AgainstToc(cdrdao.ToToc(), toc), .. CdrdaoChecks.AgainstCue(cdrdao, WhipperCue.Load(cueFile))];
                }
                catch (FormatException e)
                {
                    checks = [new Check("TOC file", e.Message, "readable")];
                }
                var bad = checks.Where(c => !c.Same).ToList();
                if (bad.Count == 0) tocPassed++;
                else
                {
                    tocFailed++;
                    problems.AddRange(bad.Select(c => $"{Path.GetExtension(tocFile)}/cue: {c.What} {c.Ours} != whipper {c.Theirs ?? "none"}"));
                }
            }

            if (problems.Count == 0)
            {
                passed++;
                if (log.Tracks.Any(t => t.V1?.IsMatch == true || t.V2?.IsMatch == true))
                    arCandidates.Add((log, AccurateRipId.From(toc, cddb)));
            }
            else
            {
                failed++;
                Console.WriteLine($"FAIL  {Rel(path)}");
                foreach (var p in problems) Console.WriteLine($"      {p}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{logs.Count} logs: {passed} fully match whipper, {failed} differ, {skipped} skipped.");
        Console.WriteLine($"cdrdao TOC files: {tocPassed} give the same TOC, catalog and ISRCs as whipper's log and cue, {tocFailed} differ, {tocMissing} missing.");
        Console.WriteLine($"{gaps} logs list a stretch before track 1 as track 0.");

        var arFailed = 0;
        if (online > 0 && arCandidates.Count > 0)
        {
            Console.WriteLine();
            // Spread the sample across the library rather than taking the first N
            // alphabetically, which would all be AC/DC.
            var step = Math.Max(1, arCandidates.Count / online);
            var sample = arCandidates.Where((_, i) => i % step == 0).Take(online).ToList();
            using var http = NewHttp();
            foreach (var (log, id) in sample)
            {
                arFailed += await CheckAccurateRipAsync(http, log, id) ? 0 : 1;
                await Task.Delay(TimeSpan.FromSeconds(1)); // be a polite client
            }
            Console.WriteLine($"AccurateRip: {sample.Count - arFailed} of {sample.Count} discs found at the URL Deadwax computes, with whipper's checksums in them.");
        }

        return failed == 0 && arFailed == 0 ? 0 : 1;

        string Rel(string p) => Path.GetRelativePath(library, p);
    }

    /// The AccurateRip ID is right if the file it names exists and holds, for each
    /// track whipper matched, the very CRC whipper matched against. Confidence
    /// is reported, not compared: it grows as people submit rips.
    internal static async Task<bool> CheckAccurateRipAsync(HttpClient http, WhipperLog log, AccurateRipId id)
    {
        var name = Path.GetFileNameWithoutExtension(log.Path);
        IReadOnlyList<AccurateRipBlock>? blocks;
        try { blocks = await AccurateRipDatabase.FetchAsync(http, id); }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"FAIL  {name}: {e.Message}");
            return false;
        }
        if (blocks is null)
        {
            Console.WriteLine($"FAIL  {name}: {id.FileName} not in the database");
            return false;
        }

        int matched = 0, missing = 0;
        foreach (var track in log.Tracks)
        {
            foreach (var ar in new[] { track.V1, track.V2 })
            {
                if (ar?.IsMatch != true || ar.RemoteCrc is not { } crc) continue;
                var found = blocks.Any(b => track.Number - 1 < b.Tracks.Count && b.Tracks[track.Number - 1].Crc == crc);
                if (found) matched++; else missing++;
            }
        }

        // A disc whipper found no match for can have been submitted since
        // (Shout at the Devil: none at rip time, 2 pressings by 2026-09-24).
        // The file being there at Deadwax's URL is then the whole check.
        if (matched == 0 && missing == 0)
        {
            Console.WriteLine($"OK    {name}: {id.FileName}, {blocks.Count} pressing(s); whipper had no match at rip time, so no logged CRCs to look for");
            return true;
        }

        var ok = missing == 0;
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")}  {name}: {id.FileName}, {blocks.Count} pressing(s), {matched} logged CRCs found{(missing > 0 ? $", {missing} MISSING" : "")}");
        return ok;
    }
}
