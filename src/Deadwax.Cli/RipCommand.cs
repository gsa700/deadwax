using Deadwax.Core;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// deadwax rip: a whole disc into a library folder. Writes nothing if the album
/// folder exists already. --post-rip then runs his library tools, as the
/// wizard's post-rip hook did, and only after a rip where every track agreed.
internal static class RipCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var device = options.Value("--device");
        var library = options.Value("--library");
        var conventions = options.Value("--conventions") ?? "~/Music";
        var release = options.Value("--release");
        var year = options.Value("--year");
        var offset = options.Value("--offset");
        var offline = options.Flag("--offline");
        var postRip = options.Flag("--post-rip");
        var unbox = options.Flag("--unbox");
        options.Rest();
        if (library is null) return Fail("--library DIR is required (use a scratch folder until G5 has passed)");

        var session = new RipSession(
            new RipOptions
            {
                Device = device,
                Library = Home(library),
                ConventionsLibrary = Home(conventions),
                ReleaseId = release,
                Year = year,
                Offset = offset is null ? null : int.Parse(offset),
                AccurateRip = !offline,
            },
            say: Console.WriteLine,
            progress: Progress());

        try
        {
            var result = await session.RunAsync();
            if (!Console.IsErrorRedirected) Console.Error.Write("\r\x1b[K");
            Console.WriteLine();
            Console.WriteLine(result.AllOk
                ? $"Done: {result.Tracks.Count} tracks, every one read twice alike. {result.AlbumDirectory}"
                : $"Done with problems: {result.Tracks.Count(t => !t.CopyOk)} track(s) never read the same twice. {result.AlbumDirectory}");
            if (!result.AllOk) return 1;
            if (!postRip) return 0;
            Console.WriteLine();
            var after = await PostRip.RunAsync(result.AlbumDirectory, unbox, Console.WriteLine);
            foreach (var note in after.Notes) Console.WriteLine($"Note: {note}");
            Console.WriteLine(after.AuditClean ? $"Post-rip finished: {after.AlbumDirectory}" : "Post-rip finished; music-audit found issues (above).");
            return after.AuditClean ? 0 : 1;
        }
        catch (ReleaseChoiceNeeded choice)
        {
            Console.WriteLine("This disc is on more than one MusicBrainz release. Rerun with --release ID:");
            foreach (var c in choice.Candidates) Console.WriteLine($"  {c}");
            return 2;
        }
        catch (RipException e)
        {
            return Fail(e.Message);
        }
    }

    private static Action<int, string, int, int> Progress()
    {
        var last = (Track: 0, Pass: "", Step: -1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return (track, pass, done, total) =>
        {
            var pct = done * 100 / total;
            if (Console.IsErrorRedirected)
            {
                var step = pct / 10 * 10;
                if ((track, pass, step) == last) return;
                if (last.Track != track || last.Pass != pass) clock.Restart();
                last = (track, pass, step);
                Console.Error.WriteLine($"  track {track} {pass} {step,3}%  {clock.Elapsed:mm\\:ss}");
                return;
            }
            Console.Error.Write($"\r\x1b[K  track {track} {pass} {pct,3}%");
        };
    }
}
