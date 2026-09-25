using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Deadwax.Core;

/// After a clean rip: his three library tools, called as they are, in the
/// order the wizard's post-rip hook calls them (spec §7: v1 calls them, it does
/// not rewrite them).
///
/// 1. music-unbox, when the folder is a disc of a set ("(Disc N of M)"): the
///    plan always; the split only when asked. It retags the disc as its own
///    album, fetches that album's front, and renames the folder "YYYY - Album".
/// 2. music-backart: back.jpg for the album.
/// 3. music-audit over the library: must report 0 issues.
public static partial class PostRip
{
    public sealed record Result(string AlbumDirectory, bool AuditClean, IReadOnlyList<string> Notes);

    public static async Task<Result> RunAsync(string albumDir, bool unbox, Action<string> say, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var root = Path.GetDirectoryName(Path.GetDirectoryName(albumDir)!)!;
        var artist = Path.GetFileName(Path.GetDirectoryName(albumDir)!);
        var folder = Path.GetFileName(albumDir);

        if (DiscOfSet().IsMatch(folder) && Tool("music-unbox") is { } unboxTool)
        {
            say("This disc is part of a set; asking music-unbox for a plan...");
            var (_, plan) = await RunToolAsync(unboxTool, ["--only", artist, root], ct);
            var planned = plan.Contains("->", StringComparison.Ordinal);
            foreach (var line in plan.Split('\n').Where(l => l.Contains("->") || l.Contains('!') || l.Contains("left alone") || l.Contains("KEEP")))
                say("  " + line.Trim());
            if (!planned) notes.Add("music-unbox: nothing to split; left as a set.");
            else if (!unbox) notes.Add("music-unbox would split this disc into its own album; rerun with --unbox to do it.");
            else
            {
                say("Splitting it into its own album...");
                var (rc, output) = await RunToolAsync(unboxTool, ["--only", artist, "--write", root], ct);
                if (rc != 0) notes.Add($"music-unbox --write exited {rc}.");
                if (output.Contains("SLIPCASE", StringComparison.Ordinal))
                    notes.Add("music-unbox kept the slipcase front; often a Cover Art Archive blip, check the cover.");
                // music-unbox renames to "YYYY - Album" beside the old folder and
                // prints "  ok  YYYY - Album  (N tracks...".
                var renamed = Renamed().Match(output);
                if (renamed.Success && Directory.Exists(Path.Combine(root, artist, renamed.Groups[1].Value)))
                {
                    folder = renamed.Groups[1].Value;
                    albumDir = Path.Combine(root, artist, folder);
                    say($"Now {artist}/{folder}");
                }
                else if (!Directory.Exists(albumDir)) notes.Add("music-unbox renamed the folder, but not in a form Deadwax recognised.");
            }
        }

        if (Tool("music-backart") is { } backart)
        {
            say("Back cover (music-backart)...");
            await RunToolAsync(backart, ["--only", $"{artist}/{folder}", "--write", root], ct);
            if (!File.Exists(Path.Combine(albumDir, "back.jpg"))) notes.Add("No back.jpg: the Cover Art Archive may not have one.");
        }

        var clean = true;
        if (Tool("music-audit") is { } audit)
        {
            say($"Library audit (music-audit {root})...");
            var (rc, output) = await RunToolAsync(audit, [root], ct);
            clean = rc == 0;
            var tail = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(clean ? 2 : 30);
            foreach (var line in tail) say("  " + line);
        }
        return new Result(albumDir, clean, notes);
    }

    /// His tools live in ~/.local/bin, which a non-login environment may not
    /// have on PATH.
    private static string? Tool(string name)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name);
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    private static async Task<(int Code, string Output)> RunToolAsync(string tool, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdout + await stderr);
    }

    [GeneratedRegex(@"\(Disc \d+ of \d+\)")]
    private static partial Regex DiscOfSet();

    [GeneratedRegex(@"(?m)^ +ok +(\d{4} - .+?[^ ]) +\(\d+ tracks")]
    private static partial Regex Renamed();
}
