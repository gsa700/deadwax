using System.Diagnostics;
using System.Text.RegularExpressions;
using Deadwax.Metadata;
using Deadwax.Verify;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// Gate G5: a Deadwax rip against whipper's rip of the same disc, file by file.
internal static partial class CompareCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var rest = options.Rest();
        if (rest.Count != 2) return Fail("usage: deadwax compare DEADWAX_ALBUM_DIR WHIPPER_ALBUM_DIR");
        var (ours, theirs) = (Home(rest[0]), Home(rest[1]));

        var ourFlacs = Flacs(ours);
        var theirFlacs = Flacs(theirs);
        var problems = 0;
        void Report(bool same, string what, string detail = "")
        {
            if (!same) problems++;
            Console.WriteLine($"  {(same ? "same" : "DIFF")}  {what}{(detail.Length > 0 ? "  " + detail : "")}");
        }

        Report(ourFlacs.Keys.SequenceEqual(theirFlacs.Keys), "track numbers", $"{ourFlacs.Count} vs {theirFlacs.Count}");
        foreach (var n in ourFlacs.Keys.Intersect(theirFlacs.Keys))
        {
            var (a, b) = (ourFlacs[n], theirFlacs[n]);
            var sameAudio = FlacInfo.AudioMd5(a) == FlacInfo.AudioMd5(b);
            var sameFrames = sameAudio && FramesEqual(a, b);
            Report(sameFrames, $"track {n} FLAC", sameFrames ? "byte-identical audio frames" : sameAudio ? "same audio, different frames" : "DIFFERENT AUDIO");

            var ta = await TagsAsync(a);
            var tb = await TagsAsync(b);
            var diffs = ta.Select(t => t.Key).Union(tb.Select(t => t.Key)).Distinct()
                .Where(k => !ta.Where(t => t.Key == k).Select(t => t.Value).Order().SequenceEqual(tb.Where(t => t.Key == k).Select(t => t.Value).Order()))
                .Select(k => $"{k}: [{string.Join(" | ", ta.Where(t => t.Key == k).Select(t => t.Value))}] vs [{string.Join(" | ", tb.Where(t => t.Key == k).Select(t => t.Value))}]")
                .ToList();
            Report(diffs.Count == 0, $"track {n} tags", diffs.Count == 0 ? "" : string.Join("; ", diffs));
            var sameName = Path.GetFileName(a) == Path.GetFileName(b);
            Report(sameName, $"track {n} file name", sameName ? "" : $"{Path.GetFileName(a)} vs {Path.GetFileName(b)}");
        }

        // Logs: the same CRCs and AccurateRip checksums, track by track.
        var logA = Directory.EnumerateFiles(ours, "*.log").FirstOrDefault();
        var logB = Directory.EnumerateFiles(theirs, "*.log").FirstOrDefault();
        if (logA is not null && logB is not null)
        {
            var (la, lb) = (WhipperLog.Load(logA), WhipperLog.Load(logB));
            Report(la.MusicBrainzId == lb.MusicBrainzId && la.CddbId == lb.CddbId, "log disc IDs");
            Report(la.ToToc().Tracks.SequenceEqual(lb.ToToc().Tracks) && la.ToToc().LeadoutLsn == lb.ToToc().LeadoutLsn, "log TOC");
            foreach (var t in la.Tracks)
            {
                var o = lb.Tracks.FirstOrDefault(x => x.Number == t.Number);
                Report(o?.CopyCrc == t.CopyCrc, $"log track {t.Number} copy CRC");
                if (o?.V2?.LocalCrc is { } v2) Report(t.V2?.LocalCrc == v2, $"log track {t.Number} AccurateRip v2");
                if (o?.V1?.LocalCrc is { } v1) Report(t.V1?.LocalCrc == v1, $"log track {t.Number} AccurateRip v1");
            }
            Report(Deadwax.Output.RipLog.Verify(File.ReadAllText(logA)), "log SHA-256 line");
        }
        else Report(false, "logs", "missing");

        foreach (var ext in new[] { ".m3u", ".cue", ".toc" })
        {
            var fa = Directory.EnumerateFiles(ours, "*" + ext).FirstOrDefault();
            var fb = Directory.EnumerateFiles(theirs, "*" + ext).FirstOrDefault();
            if (fa is null || fb is null) { Report(false, ext, "missing"); continue; }
            var same = File.ReadAllText(fa) == File.ReadAllText(fb);
            Report(same || ext == ".cue", ext, same ? "identical" : ext == ".cue" ? "differs (expected: Deadwax drops whipper's quirks; structure is checked by check-sidecars)" : "differs");
        }

        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "G5: PASS, the Deadwax rip matches whipper's." : $"G5: {problems} difference(s).");
        return problems == 0 ? 0 : 1;
    }

    private static SortedDictionary<int, string> Flacs(string dir) =>
        new(Directory.EnumerateFiles(dir, "*.flac")
            .Select(f => (M: TrackNumber().Match(Path.GetFileName(f)), F: f)).Where(x => x.M.Success)
            .ToDictionary(x => int.Parse(x.M.Groups[1].Value), x => x.F));

    /// Everything after the metadata blocks: the encoded audio frames.
    private static bool FramesEqual(string a, string b) => Frames(a).AsSpan().SequenceEqual(Frames(b));

    private static byte[] Frames(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var at = 4;                                   // "fLaC"
        while (true)
        {
            var last = (bytes[at] & 0x80) != 0;
            var length = bytes[at + 1] << 16 | bytes[at + 2] << 8 | bytes[at + 3];
            at += 4 + length;
            if (last) return bytes[at..];
        }
    }

    private static async Task<List<Tag>> TagsAsync(string flac)
    {
        var start = new ProcessStartInfo("metaflac") { RedirectStandardOutput = true };
        start.ArgumentList.Add("--export-tags-to=-");
        start.ArgumentList.Add(flac);
        using var p = Process.Start(start)!;
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=', 2))
            .Where(kv => kv.Length == 2).Select(kv => new Tag(kv[0].ToUpperInvariant(), kv[1])).ToList();
    }

    [GeneratedRegex(@" - (\d{2,3}) - ")]
    private static partial Regex TrackNumber();
}
