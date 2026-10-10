using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Verify;

namespace Deadwax.Cli;

internal static class Commands
{
    private const string Usage = """
        usage:
          deadwax rip [--library DIR] [--release ID] [--year YYYY] [--device DEV] [--offset N] [--speed N]
                      [--conventions LIBRARY] [--offline] [--keep-note] [--post-rip [--unbox]]
              Rip the disc into DIR/Artist/YEAR - Album/ (DIR defaults to ~/Music): FLACs, .toc, .cue, .m3u,
              .log. Artist spellings follow LIBRARY (default ~/Music). The year
              defaults to the album's original year. Never writes over a folder.
              --keep-note keeps MusicBrainz's disambiguation note in the folder
              name, as whipper did.
              --post-rip then files a set's disc as its own album (planned
              only, unless --unbox), fetches back.jpg, and checks the album
              is filed as Preferences > Filing says.
          deadwax post-rip ALBUM_DIR [--unbox]
              The same steps on an album already ripped.
          deadwax art ALBUM_DIR [--replace]
              cover.jpg (Cover Art Archive, by the release ids in the album's
              tags) and back.jpg (this release's, else another edition's) for an album that has none,
              such as a rip made while the archive was down. --replace fetches
              both again.
          deadwax compare DEADWAX_ALBUM_DIR WHIPPER_ALBUM_DIR
              Gate G5: a Deadwax rip against whipper's rip of the same disc,
              file by file: audio frames, tags, names, logs, .m3u, .cue, .toc.
          deadwax scan [--device DEV] [--speed N] [--full] [--online] [--against ALBUM_DIR|LOG]
              Read the disc's TOC and print its IDs; --full adds catalog, ISRCs,
              CD-Text and pregaps via cdrdao (~2 min). With --against, compare them
              with the whipper log of the same disc (gate G1).
          deadwax read [--device DEV] [--tracks N,N|all] [--offset N] [--retries N] [--speed N] [--offline] [--against ALBUM_DIR]
              Read tracks securely, twice, with the drive's read offset (from
              drives.json unless given). With --against, check the audio equals
              whipper's rip: logged CRC and the FLAC's stored MD5 (gate G2), and
              AccurateRip v1/v2 checksums equal whipper's (G3). Writes nothing.
          deadwax offset [--device DEV] [--save]
              Measure the drive's read offset from a well-known disc: one track
              read once, every offset from -2000 to +2000 checked against
              AccurateRip. --save records it in ~/.config/deadwax/drives.json.
          deadwax check-audio [LIBRARY|LOG] [--limit N]
              Decode whipper's FLACs and check Deadwax's copy CRC and AccurateRip
              v1/v2 checksums of that audio equal the logged ones (G2/G3, no drive).
          deadwax check-tags [LIBRARY] [--limit N] [--verbose]
              Build each album's tags from MusicBrainz and the disc's .toc and
              compare them with the tags in the library (G4). Caches releases in
              ~/.cache/deadwax/musicbrainz.
          deadwax check-sidecars [LIBRARY] [--verbose]
              Regenerate each album's .cue and .m3u from its .toc and compare
              them with whipper's (G4).
          deadwax check-logs [LIBRARY] [--online N]
              Rebuild each whipper log's TOC and check that Deadwax computes the
              same disc IDs whipper recorded. --online N also fetches N discs from
              AccurateRip and checks the logged checksums are in the file Deadwax
              names. LIBRARY defaults to ~/Music.
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            return args[0] switch
            {
                "rip" => await RipCommand.RunAsync(args[1..]),
                "compare" => await CompareCommand.RunAsync(args[1..]),
                "post-rip" => await PostRipCommand.RunAsync(args[1..]),
                "art" => await ArtCommand.RunAsync(args[1..]),
                "scan" => await ScanCommand.RunAsync(args[1..]),
                "read" => await ReadCommand.RunAsync(args[1..]),
                "offset" => await OffsetCommand.RunAsync(args[1..]),
                "check-logs" => await CheckLogsCommand.RunAsync(args[1..]),
                "check-audio" => await CheckAudioCommand.RunAsync(args[1..]),
                "check-tags" => await CheckTagsCommand.RunAsync(args[1..]),
                "check-sidecars" => await CheckSidecarsCommand.RunAsync(args[1..]),
                _ => Fail($"unknown command '{args[0]}'\n\n{Usage}"),
            };
        }
        catch (DriveException e)
        {
            return Fail(e.Message);
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
    }

    public static int Fail(string message)
    {
        Console.Error.WriteLine($"deadwax: {message}");
        return 2;
    }

    public static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Deadwax/0.1");
        return http;
    }

    /// A minimal option reader: flags, "--name value" pairs, and positionals.
    public sealed class Options
    {
        private readonly List<string> _args;
        public Options(string[] args) => _args = [.. args];

        public bool Flag(string name) => _args.Remove(name);

        public string? Value(string name)
        {
            var i = _args.IndexOf(name);
            if (i < 0) return null;
            if (i + 1 >= _args.Count) throw new ArgumentException($"{name} needs a value");
            var v = _args[i + 1];
            _args.RemoveRange(i, 2);
            return v;
        }

        public IReadOnlyList<string> Rest()
        {
            var unknown = _args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal));
            if (unknown is not null) throw new ArgumentException($"unknown option {unknown}");
            return _args;
        }
    }

    public static string Hex(uint v) => v.ToString("X8");

    public static string Home(string path) =>
        path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;
}
