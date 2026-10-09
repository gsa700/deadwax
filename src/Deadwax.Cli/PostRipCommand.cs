using Deadwax.Core;
using Deadwax.Output;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// deadwax post-rip: the library tools on an album already ripped.
internal static class PostRipCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var unbox = options.Flag("--unbox");
        var rest = options.Rest();
        if (rest.Count != 1) return Fail("usage: deadwax post-rip ALBUM_DIR [--unbox]");
        var dir = Path.GetFullPath(Home(rest[0]));
        if (!Directory.Exists(dir)) return Fail($"no such folder: {dir}");

        var after = await PostRip.RunAsync(dir, unbox, Console.WriteLine);
        foreach (var note in after.Notes) Console.WriteLine($"Note: {note}");
        var filed = FiledCheck.Run(after.AlbumDirectory, Filing.Current);
        foreach (var issue in filed) Console.WriteLine($"Filed: {issue}");
        Console.WriteLine(filed.Count == 0 ? $"Post-rip finished, filed correctly: {after.AlbumDirectory}" : "Post-rip finished; the album is not filed as it should be (above).");
        return filed.Count == 0 ? 0 : 1;
    }
}
