using System.Text.RegularExpressions;
using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Output;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// Gate G4, sidecars: regenerate each album's .cue and .m3u from its cdrdao TOC
/// and file names and compare them with whipper's. The .m3u must be identical.
/// The .cue must have the same structure (files, tracks, index points, disc ID,
/// ISRCs); the differences Sidecars documents as deliberate are set aside and
/// counted.
internal static partial class CheckSidecarsCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var verbose = options.Flag("--verbose");
        var rest = options.Rest();
        var root = Home(rest.Count > 0 ? rest[0] : "~/Music");

        var dirs = Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();

        int albums = 0, m3uSame = 0, cueSame = 0, pregapOnly = 0, quoted = 0;
        foreach (var dir in dirs)
        {
            var tocPath = Directory.EnumerateFiles(dir, "*.toc").FirstOrDefault();
            var cuePath = Directory.EnumerateFiles(dir, "*.cue").FirstOrDefault();
            var m3uPath = Directory.EnumerateFiles(dir, "*.m3u").FirstOrDefault();
            if (tocPath is null || cuePath is null || m3uPath is null) continue;

            var disc = CdrdaoToc.Parse(File.ReadAllText(tocPath));
            var toc = disc.ToToc();
            var files = Directory.EnumerateFiles(dir, "*.flac")
                .Select(f => (M: TrackNumber().Match(Path.GetFileName(f)), Name: Path.GetFileName(f)))
                .Where(x => x.M.Success).ToDictionary(x => int.Parse(x.M.Groups[1].Value), x => x.Name);
            var tracks = toc.Tracks.Where(t => t.IsAudio && files.ContainsKey(t.Number))
                .Select(t => new SidecarTrack(t.Number, files[t.Number], "-", null, toc.EndLsn(t) - t.StartLsn)).ToList();
            albums++;

            var name = Path.GetRelativePath(root, dir);
            var m3u = Sidecars.M3u(tracks);
            if (m3u == File.ReadAllText(m3uPath)) m3uSame++;
            else Console.WriteLine($"M3U   {name}");

            var ours = Structure(Sidecars.Cue(disc, DiscIds.Cddb(toc), "-", "-", tracks, "test"));
            var theirs = Structure(File.ReadAllText(cuePath));
            var oursNoPregap = ours.Where(l => !l.StartsWith("PREGAP", StringComparison.Ordinal)).ToList();
            var theirsNoPregap = theirs.Where(l => !l.StartsWith("PREGAP", StringComparison.Ordinal)).ToList();
            // A file name with a double quote in it cannot be written into a cue
            // correctly (whipper's lines are broken); Deadwax never makes one.
            if (files.Values.Any(f => f.Contains('"'))) { quoted++; continue; }
            if (ours.SequenceEqual(theirs)) cueSame++;
            else if (oursNoPregap.SequenceEqual(theirsNoPregap)) { cueSame++; pregapOnly++; }
            else
            {
                Console.WriteLine($"CUE   {name}");
                if (verbose)
                {
                    foreach (var l in ours.Except(theirs)) Console.WriteLine($"        deadwax  {l}");
                    foreach (var l in theirs.Except(ours)) Console.WriteLine($"        whipper  {l}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{albums} albums: .m3u identical on {m3uSame}; .cue structure identical on {cueSame} " +
                          $"({pregapOnly} of them differing only by whipper's inconsistent track-1 PREGAP); " +
                          $"{quoted} set aside for a double quote in a file name.");
        return Task.FromResult(m3uSame == albums && cueSame + quoted == albums ? 0 : 1);
    }

    /// The lines that carry structure, normalised: FILE, TRACK, INDEX, PREGAP,
    /// the disc ID, and each track's ISRCs once (whipper wrote them twice,
    /// once quoted).
    private static List<string> Structure(string cue)
    {
        var lines = new List<string>();
        var track = "";
        var isrcs = new SortedSet<string>(StringComparer.Ordinal);
        void FlushIsrcs()
        {
            foreach (var i in isrcs) lines.Add($"ISRC {track} {i}");
            isrcs.Clear();
        }
        foreach (var raw in cue.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith("TRACK ", StringComparison.Ordinal)) { FlushIsrcs(); track = l.Split(' ')[1]; lines.Add(l); }
            else if (l.StartsWith("FILE ", StringComparison.Ordinal) || l.StartsWith("INDEX ", StringComparison.Ordinal)
                     || l.StartsWith("PREGAP ", StringComparison.Ordinal) || l.StartsWith("REM DISCID ", StringComparison.Ordinal))
                lines.Add(l);
            else if (l.StartsWith("ISRC ", StringComparison.Ordinal)) isrcs.Add(l[5..].Trim().Trim('"'));
        }
        FlushIsrcs();
        return lines;
    }

    [GeneratedRegex(@" - (\d{2,3}) - ")]
    private static partial Regex TrackNumber();
}
