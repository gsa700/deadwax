using System.Diagnostics;
using System.Text.RegularExpressions;
using Deadwax.Metadata;

namespace Deadwax.Core;

/// After a clean rip, in the order the wizard's post-rip hook ran his library
/// tools (spec §7: v1 called them; they are being brought in one at a time).
///
/// 1. music-unbox, when installed and the folder is a disc of a set
///    ("(Disc N of M)"): the plan always; the split only when asked. It
///    retags the disc as its own album, fetches that album's front, and
///    renames the folder "YYYY - Album".
/// 2. back.jpg for the album: built in (BackCover) since 0.2.5; it was
///    music-backart.
/// 3. music-audit over the library, when installed: must report 0 issues.
public static partial class PostRip
{
    /// Audited: whether music-audit ran at all (it is his, not part of Deadwax).
    public sealed record Result(string AlbumDirectory, bool Audited, bool AuditClean, IReadOnlyList<string> Notes);

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

        bool clean = true, audited = false;
        if (Tool("music-audit") is { } audit)
        {
            say($"Library audit (music-audit {root})...");
            var (rc, output) = await RunToolAsync(audit, [root], ct);
            clean = rc == 0;
            audited = true;
            var tail = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(clean ? 2 : 30);
            foreach (var line in tail) say("  " + line);
        }
        return new Result(albumDir, audited, clean, notes);
    }

    /// back.jpg for one album, from the Cover Art Archive by the release ids in
    /// the album's own tags (BackCover). True when the album has a back.jpg
    /// afterwards; what went wrong, if anything, is added to notes. Never
    /// replaces one that is there.
    public static async Task<bool> BackArtAsync(string albumDir, Action<string> say, List<string> notes, CancellationToken ct = default)
    {
        var dest = Path.Combine(albumDir, "back.jpg");
        if (File.Exists(dest)) return true;
        var flac = Directory.EnumerateFiles(albumDir, "*.flac").Order(StringComparer.Ordinal).FirstOrDefault();
        var releaseId = flac is null ? null : await ArtistFolders.FirstTagAsync(flac, "MUSICBRAINZ_ALBUMID");
        var groupId = flac is null ? null : await ArtistFolders.FirstTagAsync(flac, "MUSICBRAINZ_RELEASEGROUPID");
        if (releaseId is null && groupId is null)
        {
            notes.Add("No back.jpg: no MusicBrainz ids in the tags to look it up by.");
            return false;
        }

        say("Back cover...");
        // BackCover gives up on its own after ~90 s of archive trouble; this
        // is the backstop so the window can never sit on "Into the library..."
        // indefinitely because of a back cover.
        using var limit = new CancellationTokenSource(BackArtLimit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
        BackCover.Result found;
        try
        {
            using var http = RipSession.NewHttp(15);
            using var mb = new MusicBrainzClient();
            found = await BackCover.FetchAsync(http, mb, releaseId, groupId, linked.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            notes.Add($"The back cover took longer than {BackArtLimit.TotalMinutes:0} minutes and was stopped; no back.jpg. Later: deadwax art \"{albumDir}\"");
            return false;
        }

        switch (found.Outcome)
        {
            case BackCover.Outcome.Failed:
                say($"  failed: {found.Error}");
                notes.Add($"No back.jpg: the Cover Art Archive did not answer ({found.Error}). Later: deadwax art \"{albumDir}\"");
                return false;
            case BackCover.Outcome.None:
                say("  none filed for this album or any edition of it.");
                notes.Add("No back.jpg: the Cover Art Archive has none for this album.");
                return false;
        }
        var part = dest + ".part";
        await File.WriteAllBytesAsync(part, found.Image!, ct);
        File.Move(part, dest);
        say($"  back.jpg written ({found.Image!.Length / 1024} KB) from {found.Source}");
        if (found.Sibling) notes.Add($"back.jpg is from another edition of this album, not this pressing: {found.Source}.");
        RecordSource(albumDir, found.Source!);
        return true;
    }

    /// Every back.jpg Deadwax writes, and where it came from: some are another
    /// edition's, and a year from now that should be knowable rather than a
    /// mystery. Appended, never rewritten. Losing the record never fails the rip.
    public static string SourcesFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "deadwax", "back-covers.tsv");

    private static void RecordSource(string albumDir, string source)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourcesFile)!);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm}\t{Path.GetFileName(Path.GetDirectoryName(albumDir))}/{Path.GetFileName(albumDir)}\t{source}\n";
            File.AppendAllText(SourcesFile, (File.Exists(SourcesFile) ? "" : "# when\talbum\twhere the back.jpg came from\n") + line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// Whether music-unbox is installed: the window offers the box-set split
    /// only on a computer that has it.
    public static bool UnboxAvailable => Tool("music-unbox") is not null;

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

    /// How long the back cover may take before it is stopped. Generous against
    /// BackCover's own ~90 s budget, so this only fires if something is wedged.
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
