using System.Diagnostics;
using Deadwax.Metadata;
using Deadwax.Output;

namespace Deadwax.Core;

/// After a clean rip, in the order the wizard's post-rip hook ran his library
/// tools (spec §7: v1 called them; they are being brought in one at a time).
///
/// 1. When the folder is a disc of a set ("(Disc N of M)"): the plan always,
///    the split only when asked (Unbox; it was music-unbox). It retags the
///    disc as its own album, fetches that album's front, and renames the
///    folder "YYYY - Album".
/// 2. back.jpg for the album (BackCover; it was music-backart), when chosen in
///    Preferences > Filing, and the front embedded when that is chosen.
///
/// music-audit is no longer run here (2026-10-08, his call: "it's really a
/// separate thing"). Each new album is checked by FiledCheck, which carries
/// music-audit's rules for one album; music-audit itself became a monthly
/// library-wide timer on his machine.
public static class PostRip
{
    public sealed record Result(string AlbumDirectory, IReadOnlyList<string> Notes);

    public static async Task<Result> RunAsync(string albumDir, bool unbox, Action<string> say, CancellationToken ct = default)
    {
        var notes = new List<string>();
        var root = FileNames.LibraryRoot(albumDir);

        if (Unbox.IsSetDisc(albumDir))
        {
            say("This disc is part of a set; looking up the album it originally was...");
            Unbox.Decision decision;
            using (var mb = new MusicBrainzClient())
                decision = await Unbox.PlanAsync(albumDir, mb, ct);
            if (decision.Plan is not { } plan)
            {
                say($"  left as part of the set: {decision.Reason}.");
                if (unbox) notes.Add($"Not filed as its own album: {decision.Reason}.");
            }
            else if (!unbox)
            {
                say($"  kept with the set; as its own album it would be {Path.GetFileName(plan.Target)}.");
                notes.Add($"Kept as part of the set. Filed as its own album it would be {Path.GetFileName(plan.Target)} (deadwax post-rip --unbox).");
            }
            else
            {
                say($"  -> {Path.GetFileName(plan.Target)}");
                var done = await Unbox.ApplyAsync(albumDir, plan, say, ct);
                if (!done.FrontReplaced)
                    notes.Add("Filed as its own album, but the album's front could not be fetched, so cover.jpg is still the set's. Often a Cover Art Archive blip: deadwax art --replace later.");
                albumDir = done.AlbumDirectory;
                say($"Now {Path.GetRelativePath(root, albumDir)}");
            }
        }

        if (Filing.Current.BackCover) await BackArtAsync(albumDir, say, notes, ct);

        // After Unbox, which may have replaced the front with the album's own.
        if (Filing.Current.EmbedCover && CoverEmbed.Apply(albumDir) is var embedded && embedded > 0)
            say($"Front cover embedded in {embedded} files.");

        return new Result(albumDir, notes);
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
        var releaseId = flac is null ? null : FlacTags.First(flac, "MUSICBRAINZ_ALBUMID");
        var groupId = flac is null ? null : FlacTags.First(flac, "MUSICBRAINZ_RELEASEGROUPID");
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

    /// How long the back cover may take before it is stopped. Generous against
    /// BackCover's own ~90 s budget, so this only fires if something is wedged.
    public static readonly TimeSpan BackArtLimit = TimeSpan.FromMinutes(3);
}
