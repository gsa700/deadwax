using System.Collections.Concurrent;
using System.Diagnostics;
using Deadwax.Metadata;
using Deadwax.Output;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// Spec §11 step 2's proof over a library: Deadwax's own FLAC decoder and
/// encoder against the files libFLAC made (through whipper or Deadwax).
///
/// For each FLAC: decode it with FlacDecoder (both CRCs of every frame, and
/// the MD5 libFLAC stored), encode that audio with FlacEncoder into a scratch
/// folder, decode the result again with FlacDecoder and, when it is installed,
/// with FFmpeg's own FLAC decoder: all must give the same audio. Then the size
/// of our frames against the original's. Never writes to the library.
internal static class CheckFlacEncoderCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var limitText = options.Value("--limit");
        var rest = options.Rest();
        var root = Home(rest.Count > 0 ? rest[0] : "~/Music");
        var flacs = Directory.EnumerateFiles(root, "*.flac", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
        var limit = limitText is null ? flacs.Count : int.Parse(limitText);
        if (limit < flacs.Count) flacs = Enumerable.Range(0, limit).Select(i => flacs[(int)(i * (flacs.Count / (double)limit))]).ToList();
        var ffmpeg = FindFfmpeg();
        Console.WriteLine($"{flacs.Count} FLACs; FFmpeg {(ffmpeg is null ? "not found: only Deadwax's decoder reads ours back" : "at " + ffmpeg)}");

        var scratch = Path.Combine(Path.GetTempPath(), $"deadwax-check-encoder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        var results = new ConcurrentBag<(string File, long Original, long Ours, double Seconds, string? Problem)>();
        var clock = Stopwatch.StartNew();
        var done = 0;
        try
        {
            await Parallel.ForEachAsync(flacs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (flac, ct) =>
            {
                var rel = Path.GetRelativePath(root, flac);
                try
                {
                    var pcm = FlacDecoder.DecodeToPcm(flac, out var info);
                    if ((info.SampleRate, info.Channels, info.BitsPerSample) != (44100, 2, 16)) { results.Add((rel, 0, 0, 0, null)); return; }
                    var ours = Path.Combine(scratch, $"{Guid.NewGuid():N}.flac");
                    var t = Stopwatch.StartNew();
                    FlacEncoder.Encode(ours, pcm, "Deadwax check");
                    var seconds = t.Elapsed.TotalSeconds;
                    string? problem = null;
                    if (!FlacDecoder.DecodeToPcm(ours, out _).AsSpan().SequenceEqual(pcm)) problem = "our decoder reads our file differently";
                    else if (ffmpeg is not null && !(await FfmpegDecodeAsync(ffmpeg, ours, ct)).AsSpan().SequenceEqual(pcm)) problem = "FFmpeg reads our file differently";
                    var original = new FileInfo(flac).Length - FlacMetadata.Read(flac).AudioOffset;
                    var mine = new FileInfo(ours).Length - FlacMetadata.Read(ours).AudioOffset;
                    File.Delete(ours);
                    results.Add((rel, original, mine, seconds, problem));
                }
                catch (Exception e) when (e is IOException or OutputException)
                {
                    results.Add((rel, 0, 0, 0, e.Message));
                }
                var n = Interlocked.Increment(ref done);
                if (n % 50 == 0) Console.Error.WriteLine($"  {n}/{flacs.Count} ({clock.Elapsed:h\\:mm\\:ss})");
            });
        }
        finally
        {
            Directory.Delete(scratch, true);
        }

        var all = results.OrderBy(r => r.File, StringComparer.Ordinal).ToList();
        foreach (var r in all.Where(r => r.Problem is not null)) Console.WriteLine($"FAIL  {r.File}: {r.Problem}");
        var ok = all.Where(r => r.Problem is null && r.Original > 0).ToList();
        long totalOriginal = ok.Sum(r => r.Original), totalOurs = ok.Sum(r => r.Ours);
        var larger = ok.Count(r => r.Ours > r.Original);
        var worst = ok.OrderByDescending(r => (double)r.Ours / r.Original).FirstOrDefault();
        Console.WriteLine($"Decoded, re-encoded and read back: {ok.Count} of {all.Count} the same audio, {all.Count(r => r.Problem is not null)} failed, {all.Count(r => r.Problem is null && r.Original == 0)} not CD audio (skipped).");
        if (ok.Count > 0)
        {
            Console.WriteLine($"Size of the audio frames: ours {totalOurs:N0} bytes, the originals {totalOriginal:N0} ({100.0 * totalOurs / totalOriginal - 100:0.00}%).");
            Console.WriteLine($"  {larger} track(s) larger than the original; the worst {100.0 * worst.Ours / worst.Original - 100:+0.00;-0.00}% ({worst.File}).");
            Console.WriteLine($"  Encoding time: {ok.Sum(r => r.Seconds):0} s for {ok.Count} tracks, {ok.Average(r => r.Seconds):0.00} s a track.");
        }
        var failed = all.Any(r => r.Problem is not null);
        Console.WriteLine(failed ? "FAIL: problems above." : "PASS: every track decodes to the audio it promised, and encodes back to it.");
        return failed ? 1 : 0;
    }

    private static string? FindFfmpeg()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Append(Home("~/.local/bin")))
        {
            var p = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static async Task<byte[]> FfmpegDecodeAsync(string ffmpeg, string flac, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-v", "error", "-i", flac, "-f", "s16le", "-" }) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        using var buffer = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
        var errors = p.StandardError.ReadToEndAsync(ct);
        await copy;
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new IOException($"FFmpeg failed: {(await errors).Trim()}");
        return buffer.ToArray();
    }
}
