using System.Diagnostics;
using Deadwax.Drive;
using static Deadwax.Cli.Commands;

namespace Deadwax.Cli;

/// Deadwax's own subchannel pass on the disc in the drive, printed as the .toc
/// a rip would write; with --cdrdao, cdrdao's read-toc of the same disc as
/// the oracle, line by line.
internal static class SubchannelCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = new Options(args);
        var device = options.Value("--device") ?? CdDrive.DefaultDevice;
        var speed = options.Value("--speed");
        var against = options.Flag("--cdrdao");
        var outPath = options.Value("--out");
        options.Rest();

        Cdrdao.Result ours;
        var clock = Stopwatch.StartNew();
        using (var drive = CdDrive.Open(device))
        {
            if (speed is not null) drive.LimitSpeed(int.Parse(speed));
            var toc = drive.ReadToc();
            ours = SubchannelReader.Read(drive, toc, m => Console.Error.WriteLine(m));
        }
        Console.Error.WriteLine($"Deadwax read the subchannel in {clock.Elapsed.TotalSeconds:0.0} s ({ours.Version}).");
        Console.Write(ours.Text);
        if (outPath is not null) await File.WriteAllTextAsync(outPath, ours.Text);
        if (!against) return 0;

        Console.Error.WriteLine("Now cdrdao read-toc on the same disc...");
        clock.Restart();
        var theirs = await Cdrdao.ReadTocAsync(device, speed is null ? null : int.Parse(speed));
        Console.Error.WriteLine($"cdrdao read it in {clock.Elapsed.TotalSeconds:0.0} s ({theirs.Version}).");

        var a = ours.Text.Split('\n');
        var b = theirs.Text.Split('\n');
        var differences = 0;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : "<end>";
            var y = i < b.Length ? b[i] : "<end>";
            if (x == y) continue;
            if (differences++ == 0) Console.WriteLine("\nDIFFERENCES (line: Deadwax | cdrdao):");
            Console.WriteLine($"  {i + 1}: {x} | {y}");
        }
        Console.WriteLine(differences == 0 ? "\nIDENTICAL: Deadwax's .toc equals cdrdao's." : $"\n{differences} line(s) differ.");
        return differences == 0 ? 0 : 1;
    }
}
