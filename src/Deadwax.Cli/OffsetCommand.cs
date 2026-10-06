using Deadwax.Core;
using Deadwax.Verify;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// deadwax offset: measure the drive's read offset from a disc AccurateRip
/// knows (OffsetFinder), and with --save record it in ~/.config/deadwax/drives.json.
internal static class OffsetCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var device = options.Value("--device");
        var save = options.Flag("--save");
        options.Rest();

        var tty = !Console.IsErrorRedirected;
        OffsetSearch? combined = null;
        OffsetCandidate? best = null;
        for (var disc = 1; ; disc++)
        {
            // The progress line is cleared on its last tick: the finder prints
            // "Checking..." to stdout straight after, before this call returns.
            var search = await OffsetFinder.FindAsync(device, Console.WriteLine,
                tty ? (done, total) => Console.Error.Write(done >= total ? "\r\x1b[K" : $"\r  reading {done * 100 / Math.Max(total, 1)}%") : null);

            if (search.Candidates.Count == 0)
            {
                Console.WriteLine($"No offset from -{OffsetFinder.Range} to +{OffsetFinder.Range} matches AccurateRip for track {search.Track} ({search.Pressings} pressing(s) on file). Try another well-known disc.");
                return 1;
            }
            Console.WriteLine($"Offsets that match AccurateRip on track {search.Track}:");
            foreach (var c in search.Candidates) Console.WriteLine($"  {c.Offset,+6:+0;-0;0}  agreed by {c.Confidence} rip(s)");
            combined = combined?.Intersect(search) ?? search;
            if (disc > 1)
            {
                Console.WriteLine($"Allowed by every disc so far: {string.Join(", ", combined.Candidates.Select(c => c.Offset.ToString("+0;-0;0")))}");
                if (combined.Candidates.Count == 0) { Console.WriteLine("No offset fits every disc; start again with other discs."); return 1; }
            }
            best = combined.Best;
            if (best is not null) break;

            // Other pressings of this disc matched too; another disc tells them apart.
            if (!tty || Console.IsInputRedirected) { Console.WriteLine("Several offsets match; run again with another well-known disc to narrow it down."); return 1; }
            Console.WriteLine($"{combined.Candidates.Count} offsets match (other pressings of this disc). Put in ANOTHER well-known disc and press Enter, or Ctrl-C.");
            Console.ReadLine();
            await Task.Delay(TimeSpan.FromSeconds(3));   // let the drive settle on the new disc
            search = null!;
        }
        var searchDrive = combined!.Drive;
        Console.WriteLine($"Read offset: {best.Offset:+0;-0;0}");
        if (save && searchDrive is { } d)
        {
            DriveOffsets.Write(d.Vendor, d.Model, d.Revision, best.Offset);
            Console.WriteLine($"Saved for {DriveOffsets.Key(d.Vendor, d.Model, d.Revision)} in {DriveOffsets.DefaultPath}.");
        }
        else if (save) Console.WriteLine("The drive gave no identity, so nothing was saved.");
        return 0;
    }
}
