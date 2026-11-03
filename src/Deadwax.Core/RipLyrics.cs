using System.Text.Json;
using System.Text.Json.Serialization;
using Deadwax.Metadata;

namespace Deadwax.Core;

// Lyrics at rip time, from LRCLIB (spec §11, "Lyrics at rip time"; his idea,
// 2026-11-02: "it's too bad the lyrics lookup is so flaky or we could
// incorporate it right into the ripping process").
//
// NEVER PART OF THE RIP. Asked after the rip and its post-rip steps, so the
// names are the filed ones (Unbox may have retagged and renamed the folder),
// and in the background: a disc is never late because of lyrics. Each track is
// asked by its own tags (ARTIST, TITLE, ALBUM) and its exact length, from the
// FLAC's STREAMINFO: the best case there is for LRCLIB's exact lookup and for
// the rule that lets a match in unasked (same artist and title, length within
// 2 s: Spindle's LrcLibClient.Fit, Marginal's rule).
//
// WHAT IS WRITTEN, as Marginal writes it: synced lyrics as a .lrc beside the
// FLAC, which AlbumWall reads first, and their words (no times) in the LYRICS
// tag; plain lyrics, when there are no synced ones and that is chosen, in the
// tag only. Nothing already there is written over: a track with a .lrc or a
// LYRICS tag is passed over. A match that does not fit well enough is not
// used; Marginal is where a person looks at it.
//
// FLAKINESS IS A QUEUE, NOT A FAILURE. What LRCLIB does not ANSWER (an error,
// a timeout, a stall: LrcLibException, after the client's own three tries) is
// kept in a queue beside the settings and asked again later; what it answers
// with nothing is an answer, and is not asked again. Three tracks in a row it
// does not answer and it is taken to be down for now: the rest wait too.
//
// The rip's .log is NOT touched: it is the record that the rip was verified,
// written when it was; lyrics are reported in the engine log and Deadwax's own.

/// One track to ask about, by what it is filed as. `Tries` and `NotBefore`
/// are the queue's: how many times LRCLIB has not answered, and when to ask
/// again.
public sealed record LyricsJob(string Path, string Artist, string Title, string Album, double Seconds)
{
    public int Tries { get; init; }
    public DateTimeOffset NotBefore { get; init; }
}

public enum LyricsOutcome
{
    /// Lyrics written: a .lrc and the tag, or the tag alone.
    Written,
    /// LRCLIB has none for it (or only an instrumental, or plain words when
    /// plain is not wanted). An answer: not asked again.
    NoneFound,
    /// LRCLIB has something, but not a fit good enough to use unasked.
    NotSure,
    /// LRCLIB did not answer: asked again later.
    NotAnswered,
    /// It has lyrics already, or the file has gone.
    Skipped,
}

public static class RipLyrics
{
    /// Every track in an album folder, by its tags and length. A file with no
    /// title or artist is left out: there is nothing to ask by.
    public static IReadOnlyList<LyricsJob> JobsFor(string albumDir)
    {
        var jobs = new List<LyricsJob>();
        foreach (var path in Directory.EnumerateFiles(albumDir, "*.flac").Order(StringComparer.Ordinal))
        {
            try
            {
                var flac = FlacMetadata.Read(path);
                var title = flac.First("TITLE");
                var artist = flac.First("ARTIST") ?? flac.First("ALBUMARTIST");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) continue;
                jobs.Add(new LyricsJob(path, artist, title, flac.First("ALBUM") ?? "", Seconds(flac.StreamInfoBlock)));
            }
            catch (IOException) { /* not a FLAC we can read: nothing to ask about */ }
        }
        return jobs;
    }

    /// The length in seconds from a STREAMINFO block: sample rate (20 bits
    /// from byte 10) and total samples (36 bits from byte 13). 0 when unknown.
    public static double Seconds(byte[] streamInfo)
    {
        if (streamInfo.Length < 18) return 0;
        var rate = (streamInfo[10] << 12) | (streamInfo[11] << 4) | (streamInfo[12] >> 4);
        var samples = ((long)(streamInfo[13] & 0x0F) << 32) | ((long)streamInfo[14] << 24)
                    | ((long)streamInfo[15] << 16) | ((long)streamInfo[16] << 8) | streamInfo[17];
        return rate > 0 ? (double)samples / rate : 0;
    }

    /// The .lrc that goes with a track.
    public static string LrcPath(string flac) => System.IO.Path.ChangeExtension(flac, ".lrc");

    /// Asks LRCLIB about one track and writes what fits. The note says what
    /// happened, in words for the log.
    public static async Task<(LyricsOutcome Outcome, string Note)> FetchAsync(
        LrcLibClient lrc, LyricsJob job, bool plainToo, CancellationToken ct = default)
    {
        if (!File.Exists(job.Path)) return (LyricsOutcome.Skipped, "the file has gone");
        if (File.Exists(LrcPath(job.Path)) || !string.IsNullOrWhiteSpace(FlacTags.First(job.Path, "LYRICS")))
            return (LyricsOutcome.Skipped, "it has lyrics already");

        var length = TimeSpan.FromSeconds(job.Seconds);
        var results = new List<LrcLibResult>();
        try
        {
            // The exact lookup first; a search only when that gave nothing that fits.
            if (await lrc.GetAsync(job.Artist, job.Title, job.Album, length, ct) is { } exact) results.Add(exact);
            if (!results.Any(r => LrcLibClient.Fit(r, job.Artist, job.Title, length).Automatic))
                results.AddRange((await lrc.SearchAsync(job.Artist, job.Title, ct)).Where(r => results.All(x => x.Id != r.Id)));
        }
        catch (LrcLibException e)
        {
            return (LyricsOutcome.NotAnswered, e.Message);
        }

        var best = LrcLibClient.Rank(results, job.Artist, job.Title, length).FirstOrDefault();
        if (best is null) return (LyricsOutcome.NoneFound, "LRCLIB has none");
        if (!best.Automatic) return (LyricsOutcome.NotSure, $"LRCLIB has a near miss ({best.Text}); look at it in Marginal");
        if (best.Result.Instrumental) return (LyricsOutcome.NoneFound, "an instrumental");

        if (best.Result.HasSynced)
        {
            WriteLrc(job.Path, best.Result.Synced!);
            FlacTags.Update(job.Path, new Dictionary<string, string> { ["LYRICS"] = Lrc.Plain(best.Result.Synced) }, []);
            return (LyricsOutcome.Written, "synced: a .lrc and the words in the tag");
        }
        if (best.Result.HasPlain && plainToo)
        {
            FlacTags.Update(job.Path, new Dictionary<string, string> { ["LYRICS"] = Lrc.Tidy(best.Result.Plain) }, []);
            return (LyricsOutcome.Written, "plain: the words in the tag");
        }
        return (LyricsOutcome.NoneFound, "only plain lyrics, and plain is not wanted");
    }

    /// Written beside itself and moved into place, never over a .lrc that is
    /// there (someone may have put one in since).
    private static void WriteLrc(string flac, string synced)
    {
        var target = LrcPath(flac);
        var temp = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target)!, "." + System.IO.Path.GetFileName(target) + ".new");
        File.WriteAllText(temp, Lrc.Tidy(synced) + "\n");
        try { File.Move(temp, target, overwrite: false); }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
}

/// The tracks still to ask about, kept beside the settings so a lookup LRCLIB
/// did not answer is asked again later, after a restart too. One run at a
/// time (RunAsync says when it is already running).
public sealed class LyricsQueue(string file, TimeSpan? pace = null)
{
    /// The pause between one track and the next. LRCLIB answered 503 after two
    /// tracks asked back to back, both questions each (James Taylor, Greatest
    /// Hits, 2026-11-02); this runs in the background, so there is no hurry.
    private readonly TimeSpan _pace = pace ?? TimeSpan.FromSeconds(2);

    /// When a track LRCLIB did not answer is asked again, by how many times it
    /// has not: 10 min, 1 h, 6 h, then a day; after that it is given up.
    private static readonly TimeSpan[] Waits =
        [TimeSpan.FromMinutes(10), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromDays(1), TimeSpan.FromDays(1), TimeSpan.FromDays(1)];

    /// Tracks in a row LRCLIB may not answer before a run stops.
    private const int DownAfter = 3;

    private readonly object _gate = new();
    private int _running;

    public int Count { get { lock (_gate) return Load().Count; } }

    public void Add(IEnumerable<LyricsJob> jobs)
    {
        lock (_gate)
        {
            var list = Load();
            foreach (var j in jobs)
                if (!list.Any(x => x.Path == j.Path)) list.Add(j);
            Save(list);
        }
    }

    /// Asks about every track that is due, one at a time; `now` is for tests.
    /// Returns how many were written; false in Ran when a run was already going.
    public async Task<(bool Ran, int Written)> RunAsync(LrcLibClient lrc, bool plainToo, Action<string> say,
                                                        DateTimeOffset? now = null, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return (false, 0);
        try
        {
            var written = 0;
            var inARow = 0;
            List<LyricsJob> due;
            lock (_gate) due = Load().Where(j => j.NotBefore <= (now ?? DateTimeOffset.Now)).ToList();
            var first = true;
            foreach (var job in due)
            {
                ct.ThrowIfCancellationRequested();
                if (!first && _pace > TimeSpan.Zero) await Task.Delay(_pace, ct);
                first = false;
                var (outcome, note) = await RipLyrics.FetchAsync(lrc, job, plainToo, ct);
                var name = System.IO.Path.GetFileName(job.Path);
                lock (_gate)
                {
                    var list = Load();
                    list.RemoveAll(x => x.Path == job.Path);
                    if (outcome == LyricsOutcome.NotAnswered)
                    {
                        inARow++;
                        if (job.Tries < Waits.Length)
                            list.Add(job with { Tries = job.Tries + 1, NotBefore = (now ?? DateTimeOffset.Now) + Waits[job.Tries] });
                        else
                            note += "; given up after asking " + (job.Tries + 1) + " times";
                    }
                    else inARow = 0;
                    Save(list);
                }
                if (outcome == LyricsOutcome.Written) written++;
                say($"lyrics: {name}: {(outcome == LyricsOutcome.NotAnswered ? "LRCLIB did not answer, asked again later (" + note + ")" : note)}");
                if (inARow >= DownAfter)
                {
                    say("lyrics: LRCLIB is not answering; the rest wait for later");
                    break;
                }
            }
            return (true, written);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private List<LyricsJob> Load()
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize(File.ReadAllText(file), LyricsQueueJson.Default.ListLyricsJob) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];   // a queue that cannot be read is lyrics not asked for: never a reason to fail
        }
    }

    private void Save(List<LyricsJob> list)
    {
        try
        {
            if (list.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, LyricsQueueJson.Default.ListLyricsJob));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* asked again next time it can be saved */ }
    }
}

[JsonSerializable(typeof(List<LyricsJob>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class LyricsQueueJson : JsonSerializerContext;
