using System.Text.RegularExpressions;
using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Verify;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// Gate G2: read tracks securely, twice, and check the audio is the audio
/// whipper ripped. Writes nothing.
internal static partial class ReadCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var device = options.Value("--device") ?? CdDrive.DefaultDevice;
        var tracksText = options.Value("--tracks") ?? "all";
        var offsetText = options.Value("--offset");
        var retriesText = options.Value("--retries");
        var against = options.Value("--against");
        var offline = options.Flag("--offline");
        options.Rest();

        DriveIdentity? identity;
        Toc toc;
        using (var drive = CdDrive.Open(device))
        {
            identity = drive.ReadIdentity();
            toc = drive.ReadToc();
        }

        int offset;
        if (offsetText is not null) offset = int.Parse(offsetText);
        else if (identity is not null && WhipperConfig.ReadOffset(identity.Vendor, identity.Model, identity.Revision) is { } o) offset = o;
        else return Fail($"no read offset known for {identity?.ToString() ?? device}; give one with --offset");
        var retries = retriesText is null ? SecureReader.DefaultMaxRetries : int.Parse(retriesText);

        var numbers = tracksText == "all"
            ? toc.Tracks.Where(t => t.IsAudio).Select(t => t.Number).ToList()
            : tracksText.Split(',').Select(int.Parse).ToList();

        WhipperLog? log = null;
        var flacs = new Dictionary<int, string>();
        if (against is not null)
        {
            var dir = Home(against);
            var logPath = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.log").FirstOrDefault() : null;
            if (logPath is null) return Fail($"no whipper .log in {against}");
            log = WhipperLog.Load(logPath);
            if (log.MusicBrainzId != DiscIds.MusicBrainz(toc))
                return Fail($"the disc in the drive is not the one in {against} (MusicBrainz ID {DiscIds.MusicBrainz(toc)}, log says {log.MusicBrainzId})");
            foreach (var f in Directory.EnumerateFiles(dir, "*.flac"))
                if (TrackNumber().Match(Path.GetFileName(f)) is { Success: true } m) flacs.TryAdd(int.Parse(m.Groups[1].Value), f);
        }

        // AccurateRip: fetched once, before reading, so a slow network does not
        // hold the drive.
        var audioTracks = toc.IdTracks.Where(t => t.IsAudio).ToList();
        var arId = AccurateRipId.From(toc, DiscIds.Cddb(toc));
        IReadOnlyList<AccurateRipBlock>? arBlocks = null;
        var arNote = "";
        if (!offline)
        {
            try
            {
                using var http = NewHttp();
                arBlocks = await AccurateRipDatabase.FetchAsync(http, arId);
                arNote = arBlocks is null ? "not in the AccurateRip database" : $"AccurateRip: {arBlocks.Count} pressing(s) on file";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                arNote = $"AccurateRip unreachable ({e.Message})";
            }
        }

        Console.WriteLine($"Drive  {identity?.ToString() ?? device}, read offset {offset:+0;-0;0}, up to {retries} retries per sector");
        if (arNote.Length > 0) Console.WriteLine($"       {arNote}");
        Console.WriteLine();
        Console.WriteLine(" #  length     test CRC  copy CRC  pass  speed   repairs skips  AR v1     AR v2     AccurateRip" +
                          (log is null ? "" : "   whipper CRC  FLAC MD5  AR vs whipper"));

        var failures = 0;
        var arFailures = 0;
        using var reader = SecureReader.Open(device);
        foreach (var n in numbers)
        {
            var track = toc.Track(n);
            var sectors = toc.EndLsn(track) - track.StartLsn;
            var seconds = sectors / (double)Toc.SectorsPerSecond;

            var (testAudio, testReport) = reader.ReadTrack(toc, n, offset, retries, Progress($"track {n} test"));
            var testCrc = AudioChecks.Crc32(testAudio);
            testAudio = null;
            var (copyAudio, copyReport) = reader.ReadTrack(toc, n, offset, retries, Progress($"track {n} copy"));
            if (!Console.IsErrorRedirected) Console.Error.Write("\r\x1b[K");

            var copyCrc = AudioChecks.Crc32(copyAudio);
            var passOk = testCrc == copyCrc && copyReport.Skips == 0 && testReport.Skips == 0;
            var speed = seconds / Math.Max(copyReport.Elapsed.TotalSeconds, 0.001);
            var line = $"{n,2}  {Clock(sectors),-9}  {Hex(testCrc)}  {Hex(copyCrc)}  {(passOk ? "OK  " : "FAIL")}  {speed,4:0.0}x  {copyReport.Repairs,7} {copyReport.Skips,5}";
            if (!passOk) failures++;

            var (v1, v2) = AccurateRipChecksum.Compute(copyAudio, n == audioTracks[0].Number, n == audioTracks[^1].Number);
            var verdict = AccurateRipMatch.For(arBlocks, audioTracks.FindIndex(t => t.Number == n), n, v1, v2);
            var arText = arBlocks is null ? "-"
                : !verdict.IsAccurate ? "no match"
                : verdict.V2Confidence >= verdict.V1Confidence ? $"v2, {verdict.V2Confidence} rips" : $"v1, {verdict.V1Confidence} rips";
            line += $"  {Hex(v1)}  {Hex(v2)}  {arText,-11}";

            if (log is not null)
            {
                var logged = log.Tracks.FirstOrDefault(t => t.Number == n);
                var crcSame = logged?.CopyCrc == copyCrc;
                string md5Col;
                if (flacs.TryGetValue(n, out var flac) && FlacInfo.AudioMd5(flac) is { } md5)
                {
                    var md5Same = md5 == AudioChecks.Md5(copyAudio);
                    md5Col = md5Same ? "same" : "DIFF";
                    if (!md5Same) failures++;
                }
                else md5Col = "(no FLAC)";
                if (!crcSame) failures++;
                line += $"   {(crcSame ? "same" : "DIFF " + (logged?.CopyCrc is { } c ? Hex(c) : "none")),-11}  {md5Col,-8}";

                // G3: whipper logged its own local checksums; ours must equal them.
                var arChecks = new List<bool>();
                if (logged?.V1?.LocalCrc is { } l1) arChecks.Add(l1 == v1);
                if (logged?.V2?.LocalCrc is { } l2) arChecks.Add(l2 == v2);
                var arSame = arChecks.All(x => x);
                if (!arSame) arFailures++;
                line += $"  {(arChecks.Count == 0 ? "(none logged)" : arSame ? "same" : "DIFF")}";
            }
            Console.WriteLine(line);
        }

        Console.WriteLine();
        if (log is not null)
        {
            Console.WriteLine(failures == 0
                ? $"G2: PASS, {numbers.Count} track(s) read twice, identical, and the same audio as whipper's rip."
                : $"G2: FAIL, {failures} problem(s).");
            Console.WriteLine(arFailures == 0
                ? "G3: PASS, AccurateRip v1/v2 checksums equal the ones whipper logged."
                : $"G3: FAIL, {arFailures} track(s) with different AccurateRip checksums.");
        }
        return failures + arFailures == 0 ? 0 : 1;
    }

    /// On a terminal, one line rewritten in place. Redirected to a file, a line
    /// every 10% with the time, so a slow disc shows where it is slow: a run
    /// on a smudged Anthology of Bread went 20 minutes with nothing to see.
    private static Action<int, int> Progress(string label)
    {
        var last = -1;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return (done, total) =>
        {
            var pct = done * 100 / total;
            if (Console.IsErrorRedirected)
            {
                var step = pct / 10 * 10;
                if (step == last) return;
                last = step;
                Console.Error.WriteLine($"  {label} {step,3}%  sector {done}/{total}  {clock.Elapsed:mm\\:ss}");
                return;
            }
            if (pct == last) return;
            last = pct;
            Console.Error.Write($"\r\x1b[K  {label} {pct,3}%");
        };
    }

    private static string Clock(int sectors)
    {
        var s = sectors / Toc.SectorsPerSecond;
        return $"{s / 60}:{s % 60:D2}.{sectors % Toc.SectorsPerSecond:D2}";
    }

    [GeneratedRegex(@" - (\d{2,3}) - ")]
    private static partial Regex TrackNumber();
}
