using System.Diagnostics;
using Deadwax.Drive;
using Deadwax.Metadata;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// Gate G4, tags: for each album in the library, build the tags Deadwax would
/// write and compare them with the tags in the FLACs whipper made (after the
/// wizard's and music-unbox's corrections, which is the state Deadwax must
/// reproduce). Box discs that music-unbox refiled have no MUSICBRAINZ_ALBUMID
/// and are counted separately: their album-level tags are §7's job.
internal static class CheckTagsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var limitText = options.Value("--limit");
        var verbose = options.Flag("--verbose");
        var rest = options.Rest();
        var root = Home(rest.Count > 0 ? rest[0] : "~/Music");
        var limit = limitText is null ? int.MaxValue : int.Parse(limitText);

        var dirs = Directory.EnumerateFiles(root, "*.log", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
        if (limit < dirs.Count)
        {
            var step = dirs.Count / (double)limit;
            dirs = Enumerable.Range(0, limit).Select(i => dirs[(int)(i * step)]).ToList();
        }

        using var mb = new MusicBrainzClient(MusicBrainzClient.DefaultCacheDir);
        var artistFolders = await ArtistFolders.ScanAsync(root);
        var remnants = 0;
        var byKey = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var examples = new Dictionary<string, List<string>>();
        int albums = 0, clean = 0, boxes = 0, tracks = 0;

        foreach (var dir in dirs)
        {
            var flacs = Directory.EnumerateFiles(dir, "*.flac").Order(StringComparer.Ordinal).ToList();
            if (flacs.Count == 0) continue;
            var first = await ReadTagsAsync(flacs[0]);
            var releaseId = first.FirstOrDefault(t => t.Key == "MUSICBRAINZ_ALBUMID")?.Value;
            if (releaseId is null) { boxes++; continue; }

            using var release = await mb.GetReleaseAsync(releaseId);
            if (release is null) { Console.WriteLine($"GONE  {Rel(dir)}: release {releaseId} no longer exists"); continue; }

            var toc = Directory.EnumerateFiles(dir, "*.toc").FirstOrDefault() is { } tocPath
                ? CdrdaoToc.Parse(await File.ReadAllTextAsync(tocPath)) : null;

            // A disc refiled out of a box by the older wizard can still carry the
            // box's album ID: the library says disc 1 of 1, the release says
            // otherwise. Its album-level tags are §7's job, like the others.
            var libraryDiscTotal = first.FirstOrDefault(t => t.Key == "DISCTOTAL")?.Value;
            if (libraryDiscTotal == "1" && release.RootElement.GetProperty("media").GetArrayLength() > 1) { remnants++; continue; }

            // The folder's year is his decision (defaulting to the original
            // year); here it stands in for the choice made on the Disc screen.
            var folderYear = Path.GetFileName(dir)[..4];
            var albumArtistId = first.FirstOrDefault(t => t.Key == "MUSICBRAINZ_ALBUMARTISTID")?.Value;
            var folderArtist = albumArtistId is null ? null : artistFolders.For(albumArtistId);

            albums++;
            var albumDiffs = 0;
            foreach (var flac in flacs)
            {
                var actual = await ReadTagsAsync(flac);
                var discId = actual.FirstOrDefault(t => t.Key == "MUSICBRAINZ_DISCID")?.Value;
                var number = int.Parse(actual.First(t => t.Key == "TRACKNUMBER").Value);
                var discNumber = actual.FirstOrDefault(t => t.Key == "DISCNUMBER") is { } dn ? int.Parse(dn.Value) : (int?)null;
                var disc = toc?.Tracks.FirstOrDefault(t => t.Number == number);

                IReadOnlyList<Tag> expected;
                try
                {
                    var medium = ReleaseTags.Medium(release.RootElement, discId, discNumber);
                    expected = LibraryConventions.Apply(
                        ReleaseTags.ForTrack(release.RootElement, medium, number, discId, disc?.Isrc ?? disc?.Text?.Isrc),
                        folderYear, folderArtist);
                }
                catch (Exception e) when (e is MusicBrainzException or InvalidOperationException)
                {
                    Note("(build failed)", $"{Rel(dir)} #{number}: {e.Message}");
                    albumDiffs++;
                    continue;
                }
                tracks++;

                foreach (var key in expected.Select(t => t.Key).Concat(actual.Select(t => t.Key)).Distinct())
                {
                    var want = expected.Where(t => t.Key == key).Select(t => t.Value).Order(StringComparer.Ordinal).ToList();
                    var have = actual.Where(t => t.Key == key).Select(t => t.Value).Order(StringComparer.Ordinal).ToList();
                    if (want.SequenceEqual(have)) continue;
                    albumDiffs++;
                    Note(key, $"{Rel(dir)} #{number}: deadwax [{string.Join(" | ", want)}]  library [{string.Join(" | ", have)}]");
                }
            }
            if (albumDiffs == 0) clean++;
        }

        Console.WriteLine();
        Console.WriteLine($"{albums} albums ({tracks} tracks) checked against MusicBrainz: {clean} with identical tags. " +
                          $"{boxes + remnants} refiled box discs skipped ({remnants} still carrying the box's album ID).");
        if (byKey.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Differences by tag (tracks):");
            foreach (var (key, count) in byKey.OrderByDescending(kv => kv.Value))
            {
                Console.WriteLine($"  {count,5}  {key}");
                foreach (var e in examples[key].Take(verbose ? 50 : 3)) Console.WriteLine($"         {e}");
            }
        }
        return byKey.Count == 0 ? 0 : 1;

        void Note(string key, string example)
        {
            byKey[key] = byKey.GetValueOrDefault(key) + 1;
            if (!examples.TryGetValue(key, out var list)) examples[key] = list = [];
            if (list.Count < 50) list.Add(example);
        }

        string Rel(string p) => Path.GetRelativePath(root, p);
    }

    private static async Task<List<Tag>> ReadTagsAsync(string flac)
    {
        var start = new ProcessStartInfo("metaflac") { RedirectStandardOutput = true };
        start.ArgumentList.Add("--export-tags-to=-");
        start.ArgumentList.Add(flac);
        using var p = Process.Start(start)!;
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2)).Where(kv => kv.Length == 2)
            .Select(kv => new Tag(kv[0].ToUpperInvariant(), kv[1])).ToList();
    }
}
