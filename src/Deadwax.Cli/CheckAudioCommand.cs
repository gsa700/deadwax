using System.Diagnostics;
using System.Text.RegularExpressions;
using Deadwax.Verify;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// Gates G2 and G3 without the drive: decode the FLACs whipper made and check
/// that Deadwax's checksums of that audio are the ones whipper logged, the
/// copy CRC and both AccurateRip CRCs. If they agree on audio whipper read,
/// they will agree on audio Deadwax reads.
internal static partial class CheckAudioCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var limitText = options.Value("--limit");
        var rest = options.Rest();
        var root = Home(rest.Count > 0 ? rest[0] : "~/Music");
        var limit = limitText is null ? int.MaxValue : int.Parse(limitText);

        var logs = File.Exists(root) ? [root]
            : Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList()
            : null;
        if (logs is null || logs.Count == 0) return Fail($"no whipper logs at {root}");

        // Spread a limited run across the library.
        if (limit < logs.Count)
        {
            var step = logs.Count / (double)limit;
            logs = Enumerable.Range(0, limit).Select(i => logs[(int)(i * step)]).ToList();
        }

        int albums = 0, tracks = 0, failed = 0, v1Checked = 0, v2Checked = 0;
        foreach (var logPath in logs)
        {
            var log = WhipperLog.Load(logPath);
            var dir = Path.GetDirectoryName(logPath)!;
            var toc = log.ToToc();
            var flacs = Directory.EnumerateFiles(dir, "*.flac")
                .Select(f => (Match: TrackNumber().Match(Path.GetFileName(f)), Path: f))
                .Where(x => x.Match.Success)
                .ToDictionary(x => int.Parse(x.Match.Groups[1].Value), x => x.Path);

            var problems = new List<string>();
            foreach (var t in log.Tracks.Where(t => t.Number >= 1))
            {
                if (!flacs.TryGetValue(t.Number, out var flac)) continue;
                var audio = await DecodeAsync(flac);
                tracks++;

                var crc = AudioChecks.Crc32(audio);
                if (t.CopyCrc is { } logged && logged != crc) problems.Add($"track {t.Number} CRC {Hex(crc)} != {Hex(logged)}");

                var (v1, v2) = AccurateRipChecksum.Compute(audio, t.Number == toc.FirstTrack, t.Number == toc.LastTrack);
                if (t.V1?.LocalCrc is { } l1)
                {
                    v1Checked++;
                    if (l1 != v1) problems.Add($"track {t.Number} AR v1 {Hex(v1)} != {Hex(l1)}");
                }
                if (t.V2?.LocalCrc is { } l2)
                {
                    v2Checked++;
                    if (l2 != v2) problems.Add($"track {t.Number} AR v2 {Hex(v2)} != {Hex(l2)}");
                }
            }

            albums++;
            var name = Path.GetFileName(dir);
            if (problems.Count == 0) Console.WriteLine($"OK    {name}");
            else
            {
                failed++;
                Console.WriteLine($"FAIL  {name}");
                foreach (var p in problems) Console.WriteLine($"      {p}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{albums} albums, {tracks} tracks decoded: {albums - failed} match whipper, {failed} differ. " +
                          $"Compared {v1Checked} AccurateRip v1 and {v2Checked} v2 checksums.");
        return failed == 0 ? 0 : 1;
    }

    /// Raw 16-bit little-endian audio from `flac -d`. A test harness only: Deadwax
    /// itself encodes with libFLAC and never needs to decode.
    private static async Task<byte[]> DecodeAsync(string path)
    {
        var start = new ProcessStartInfo("flac") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-d", "-s", "-c", "--force-raw-format", "--endian=little", "--sign=signed", path })
            start.ArgumentList.Add(a);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("flac did not start");
        using var buffer = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer);
        var errors = process.StandardError.ReadToEndAsync();
        await copy;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"flac -d failed on {path}: {await errors}");
        return buffer.ToArray();
    }

    [GeneratedRegex(@" - (\d{2,3}) - ")]
    private static partial Regex TrackNumber();
}
