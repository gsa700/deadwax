using System.Diagnostics;

namespace Deadwax.Drive;

/// Runs `cdrdao read-toc`. It takes about two minutes on the BDR-209D (1m51s for
/// 52nd Street) because it scans the subchannel for ISRCs and pregaps; whipper
/// paid the same on every rip.
///
/// A speed cap is passed as `--rspeed`, for drives that honour it. The
/// BDR-209D does not, measured 2026-10-08: it runs this subchannel scan at
/// its own speed whatever is asked, by cdrdao or by the kernel beforehand
/// (every pass 100-113 s; its audio reads do obey a cap). cdrdao's own
/// "maximum" before each track (GenericMMC::analyzeTrack) is ignored too.
public static class Cdrdao
{
    public sealed record Result(string Text, CdrdaoToc Toc, string Version);

    /// The command line, `speed` in multiples of 1x; null = the drive's own choice.
    public static IReadOnlyList<string> Arguments(string device, int? speed, string path) =>
        speed is { } n
            ? ["read-toc", "--device", device, "--rspeed", n.ToString(System.Globalization.CultureInfo.InvariantCulture), path]
            : ["read-toc", "--device", device, path];

    public static async Task<Result> ReadTocAsync(string device, int? speed = null, CancellationToken ct = default)
    {
        // cdrdao will not write over an existing file, so give it a fresh name.
        var dir = Directory.CreateTempSubdirectory("deadwax-");
        var path = Path.Combine(dir.FullName, "disc.toc");
        var version = "cdrdao";
        try
        {
            var start = new ProcessStartInfo("cdrdao")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in Arguments(device, speed, path)) start.ArgumentList.Add(a);

            Process process;
            try { process = Process.Start(start)!; }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new DriveException("cdrdao is not installed.");
            }

            using (process)
            {
                var stdout = process.StandardOutput.ReadToEndAsync(ct);
                var stderr = process.StandardError.ReadToEndAsync(ct);
                try { await process.WaitForExitAsync(ct); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    // Gone before anything else opens the drive: the kernel lets
                    // a command in flight finish first, so this is short.
                    process.WaitForExit(TimeSpan.FromSeconds(30));
                    throw;
                }
                var output = (await stdout) + (await stderr);
                var versionLine = output.Split('\n').FirstOrDefault(l => l.StartsWith("Cdrdao version ", StringComparison.Ordinal));
                version = versionLine is null ? "cdrdao" : "cdrdao " + versionLine.Split(' ')[2];
                if (process.ExitCode != 0 || !File.Exists(path))
                {
                    var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                    throw new DriveException($"cdrdao read-toc failed ({process.ExitCode}): {last}");
                }
            }

            var text = await File.ReadAllTextAsync(path, ct);
            return new Result(text, CdrdaoToc.Parse(text), version);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
