using Deadwax.Core;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// deadwax lyrics: lyrics from LRCLIB for albums already in the library, as
/// the window looks them up after a rip (RipLyrics; spec §11). His ask,
/// 2026-11-02: "add the lyrics command line too".
internal static class LyricsCommand
{
    /// Tracks in a row LRCLIB may not answer before the run stops.
    private const int DownAfter = 3;

    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var plainToo = !options.Flag("--no-plain");
        var queue = options.Flag("--queue");
        var rest = options.Rest();
        if (rest.Count != 1) return Fail("usage: deadwax lyrics FOLDER [--no-plain] [--queue]");
        var root = Path.GetFullPath(Home(rest[0]));
        if (!Directory.Exists(root)) return Fail($"no such folder: {root}");

        // Every folder holding FLACs: the one given, and each under it.
        var albums = new[] { root }.Concat(Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            .Where(d => Directory.EnumerateFiles(d, "*.flac").Any())
            .Order(StringComparer.Ordinal).ToList();
        var jobs = albums.SelectMany(RipLyrics.JobsFor).ToList();
        if (jobs.Count == 0) return Fail($"no tagged FLACs under {root}");

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        using var lrc = new LrcLibClient(userAgent: $"Deadwax/{AppVersion.Current} ( https://github.com/gsa700/deadwax )");

        var counts = new Dictionary<LyricsOutcome, int>();
        var nearMisses = new List<string>();
        var unanswered = new List<LyricsJob>();
        var notAsked = new List<LyricsJob>();
        var inARow = 0;
        var asked = 0;
        Console.WriteLine($"{jobs.Count} tracks in {albums.Count} album folder(s); asking LRCLIB about those with no lyrics, 2 s apart (Ctrl+C stops).");
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (cancel.IsCancellationRequested || inARow >= DownAfter) { notAsked.AddRange(jobs.Skip(i)); break; }
            if (asked > 0 && !HasLyrics(job))
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancel.Token); }
                catch (OperationCanceledException) { notAsked.AddRange(jobs.Skip(i)); break; }
            }
            (LyricsOutcome Outcome, string Note) result;
            try { result = await RipLyrics.FetchAsync(lrc, job, plainToo, cancel.Token); }
            catch (OperationCanceledException) { notAsked.AddRange(jobs.Skip(i)); break; }
            if (result.Outcome != LyricsOutcome.Skipped) asked++;
            counts[result.Outcome] = counts.GetValueOrDefault(result.Outcome) + 1;
            var where = Path.GetRelativePath(root, job.Path);
            switch (result.Outcome)
            {
                case LyricsOutcome.Skipped:
                    break;      // has lyrics already: said only in the count
                case LyricsOutcome.NotAnswered:
                    inARow++;
                    unanswered.Add(job);
                    Console.WriteLine($"  {where}: LRCLIB did not answer ({result.Note})");
                    if (inARow >= DownAfter) Console.WriteLine("LRCLIB is not answering; stopping here.");
                    break;
                case LyricsOutcome.NotSure:
                    inARow = 0;
                    nearMisses.Add(where);
                    Console.WriteLine($"  {where}: {result.Note}");
                    break;
                default:
                    inARow = 0;
                    Console.WriteLine($"  {where}: {result.Note}");
                    break;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Lyrics written: {counts.GetValueOrDefault(LyricsOutcome.Written)}; none on LRCLIB: {counts.GetValueOrDefault(LyricsOutcome.NoneFound)}; "
                        + $"had lyrics already: {counts.GetValueOrDefault(LyricsOutcome.Skipped)}; near misses: {nearMisses.Count}; "
                        + $"not answered: {unanswered.Count}{(notAsked.Count > 0 ? $"; not asked: {notAsked.Count}" : "")}.");
        if (nearMisses.Count > 0)
            Console.WriteLine("Near misses, to look at in Marginal:\n  " + string.Join("\n  ", nearMisses));
        var later = unanswered.Concat(notAsked).ToList();
        if (later.Count > 0)
        {
            if (queue)
            {
                new LyricsQueue(LyricsQueue.DefaultPath).Add(later);
                Console.WriteLine($"{later.Count} track(s) handed to the window's queue ({LyricsQueue.DefaultPath}): Deadwax asks again later, with lyrics on in Preferences > Filing.");
            }
            else Console.WriteLine($"{later.Count} track(s) were not asked about or not answered: run this again later, or with --queue to leave them to the window.");
        }
        return 0;
    }

    private static bool HasLyrics(LyricsJob job) =>
        File.Exists(RipLyrics.LrcPath(job.Path)) || !string.IsNullOrWhiteSpace(Deadwax.Metadata.FlacTags.First(job.Path, "LYRICS"));
}
