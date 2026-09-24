using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Verify;

namespace Deadwax.Cli;

internal static class Commands
{
    private const string Usage = """
        usage:
          deadwax scan [--device DEV] [--full] [--online] [--against ALBUM_DIR|LOG]
              Read the disc's TOC and print its IDs; --full adds catalog, ISRCs,
              CD-Text and pregaps via cdrdao (~2 min). With --against, compare them
              with the whipper log of the same disc (gate G1).
          deadwax read [--device DEV] [--tracks N,N|all] [--offset N] [--retries N] [--offline] [--against ALBUM_DIR]
              Read tracks securely, twice, with the drive's read offset (from
              whipper.conf unless given). With --against, check the audio equals
              whipper's rip: logged CRC and the FLAC's stored MD5 (gate G2), and
              AccurateRip v1/v2 checksums equal whipper's (G3). Writes nothing.
          deadwax check-audio [LIBRARY|LOG] [--limit N]
              Decode whipper's FLACs and check Deadwax's copy CRC and AccurateRip
              v1/v2 checksums of that audio equal the logged ones (G2/G3, no drive).
          deadwax check-tags [LIBRARY] [--limit N] [--verbose]
              Build each album's tags from MusicBrainz and the disc's .toc and
              compare them with the tags in the library (G4). Caches releases in
              ~/.cache/deadwax/musicbrainz.
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
                "scan" => await ScanCommand.RunAsync(args[1..]),
                "read" => await ReadCommand.RunAsync(args[1..]),
                "check-logs" => await CheckLogsCommand.RunAsync(args[1..]),
                "check-audio" => await CheckAudioCommand.RunAsync(args[1..]),
                "check-tags" => await CheckTagsCommand.RunAsync(args[1..]),
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
