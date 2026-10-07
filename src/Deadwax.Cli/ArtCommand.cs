using Deadwax.Core;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// deadwax art: cover.jpg and back.jpg for an album already in the library.
internal static class ArtCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var replace = options.Flag("--replace");
        var rest = options.Rest();
        if (rest.Count != 1) return Fail("usage: deadwax art ALBUM_DIR [--replace]");
        var dir = Path.GetFullPath(Home(rest[0]));
        if (!Directory.Exists(dir)) return Fail($"no such folder: {dir}");

        var art = await AlbumArt.FetchAsync(dir, replace, Console.WriteLine);
        foreach (var note in art.Notes) Console.WriteLine($"Note: {note}");
        Console.WriteLine($"{Path.GetFileName(dir)}: cover.jpg {(art.Front ? "yes" : "NO")}, back.jpg {(art.Back ? "yes" : "no")}");
        return art.Front ? 0 : 1;
    }
}
