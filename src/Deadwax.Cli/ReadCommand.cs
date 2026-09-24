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

        Console.WriteLine($"Drive  {identity?.ToString() ?? device}, read offset {offset:+0;-0;0}, up to {retries} retries per sector");
        Console.WriteLine();
        Console.WriteLine(" #  length     test CRC  copy CRC  pass  speed   repairs skips" + (log is null ? "" : "   whipper CRC  FLAC MD5"));

        var failures = 0;
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
            Console.Error.Write("\r\x1b[K");

            var copyCrc = AudioChecks.Crc32(copyAudio);
            var passOk = testCrc == copyCrc && copyReport.Skips == 0 && testReport.Skips == 0;
            var speed = seconds / Math.Max(copyReport.Elapsed.TotalSeconds, 0.001);
            var line = $"{n,2}  {Clock(sectors),-9}  {Hex(testCrc)}  {Hex(copyCrc)}  {(passOk ? "OK  " : "FAIL")}  {speed,4:0.0}x  {copyReport.Repairs,7} {copyReport.Skips,5}";
            if (!passOk) failures++;

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
                line += $"   {(crcSame ? "same" : "DIFF " + (logged?.CopyCrc is { } c ? Hex(c) : "none")),-11}  {md5Col}";
            }
            Console.WriteLine(line);
        }

        Console.WriteLine();
        if (log is not null)
            Console.WriteLine(failures == 0
                ? $"G2: PASS, {numbers.Count} track(s) read twice, identical, and the same audio as whipper's rip."
                : $"G2: FAIL, {failures} problem(s).");
        await Task.CompletedTask;
        return failures == 0 ? 0 : 1;
    }

    private static Action<int, int>? Progress(string label)
    {
        if (Console.IsErrorRedirected) return null;
        var last = -1;
        return (done, total) =>
        {
            var pct = done * 100 / total;
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
