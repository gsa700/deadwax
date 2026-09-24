using System.Diagnostics;

namespace Deadwax.Drive;

/// Runs `cdrdao read-toc`. It takes about two minutes on the BDR-209D (1m51s for
/// 52nd Street) because it scans the subchannel for ISRCs and pregaps; whipper
/// paid the same on every rip.
public static class Cdrdao
{
    public sealed record Result(string Text, CdrdaoToc Toc);

    public static async Task<Result> ReadTocAsync(string device, CancellationToken ct = default)
    {
        // cdrdao will not write over an existing file, so give it a fresh name.
        var dir = Directory.CreateTempSubdirectory("deadwax-");
        var path = Path.Combine(dir.FullName, "disc.toc");
        try
        {
            var start = new ProcessStartInfo("cdrdao")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "read-toc", "--device", device, path }) start.ArgumentList.Add(a);

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
                    throw;
                }
                var output = (await stdout) + (await stderr);
                if (process.ExitCode != 0 || !File.Exists(path))
                {
                    var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                    throw new DriveException($"cdrdao read-toc failed ({process.ExitCode}): {last}");
                }
            }

            var text = await File.ReadAllTextAsync(path, ct);
            return new Result(text, CdrdaoToc.Parse(text));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
