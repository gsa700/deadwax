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

        await BackArtAsync(albumDir, say, notes, ct);

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

    /// back.jpg for one album through music-backart, when it is installed.
    /// True when the album has a back.jpg afterwards; what went wrong, if
    /// anything, is added to notes.
    public static async Task<bool> BackArtAsync(string albumDir, Action<string> say, List<string> notes, CancellationToken ct = default)
    {
        if (Tool("music-backart") is not { } backart) return File.Exists(Path.Combine(albumDir, "back.jpg"));
        var root = Path.GetDirectoryName(Path.GetDirectoryName(albumDir)!)!;
        var artist = Path.GetFileName(Path.GetDirectoryName(albumDir)!);
        var folder = Path.GetFileName(albumDir);
        say("Back cover (music-backart)...");
        // The archive's image host (archive.org) sometimes drops connections
        // for minutes at a time (2026-10-06). music-backart now gives up on
        // its own after ~90 s; this is the backstop so the wizard can never
        // sit on "Into the library..." indefinitely because of a back cover.
        var (rc, output) = await RunToolAsync(backart, ["--only", $"{artist}/{folder}", "--write", root], ct, BackArtLimit);
        if (rc == TimedOut) notes.Add($"music-backart took longer than {BackArtLimit.TotalMinutes:0} minutes and was stopped; no back.jpg. Run it again later.");
        else if (output.Contains("FAILED", StringComparison.Ordinal)) notes.Add("music-backart: the Cover Art Archive did not answer; no back.jpg. Run it again later.");
        else if (!File.Exists(Path.Combine(albumDir, "back.jpg"))) notes.Add("No back.jpg: the Cover Art Archive may not have one.");
        return File.Exists(Path.Combine(albumDir, "back.jpg"));
    }

    public static bool BackArtAvailable => Tool("music-backart") is not null;

    /// His tools live in ~/.local/bin, which a non-login environment may not
    /// have on PATH.
    /// Whether any of the library tools is installed: the window hides the
    /// options that need them on a computer without them.
    public static bool ToolsAvailable => Tool("music-unbox") is not null || Tool("music-backart") is not null || Tool("music-audit") is not null;
    public static bool UnboxAvailable => Tool("music-unbox") is not null;

    private static string? Tool(string name)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name);
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }

    /// How long the back-cover fetch may take before it is killed. Generous
    /// against the tool's own ~90 s budget, so this only fires if the tool
    /// itself is wedged.
    public static readonly TimeSpan BackArtLimit = TimeSpan.FromMinutes(3);
    public const int TimedOut = -2;

    /// Runs one of his tools to completion. With a limit, a tool that is still
    /// running when it expires is killed (with its children) and reported as
    /// TimedOut; the caller's own cancellation still propagates as usual.
    private static async Task<(int Code, string Output)> RunToolAsync(string tool, string[] args, CancellationToken ct, TimeSpan? limit = null)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        using var timer = limit is { } l ? new CancellationTokenSource(l) : null;
        using var linked = timer is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var token = linked?.Token ?? ct;
        var stdout = p.StandardOutput.ReadToEndAsync(token);
        var stderr = p.StandardError.ReadToEndAsync(token);
        try
        {
            await p.WaitForExitAsync(token);
        }
        catch (OperationCanceledException) when (timer is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (TimedOut, "");
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return (p.ExitCode, await stdout + await stderr);
    }

    [GeneratedRegex(@"\(Disc \d+ of \d+\)")]
    private static partial Regex DiscOfSet();

    [GeneratedRegex(@"(?m)^ +ok +(\d{4} - .+?[^ ]) +\(\d+ tracks")]
    private static partial Regex Renamed();
}
