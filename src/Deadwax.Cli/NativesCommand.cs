using Deadwax.Drive;

namespace Deadwax.Cli;

/// Which libcdio and libcdio-paranoia this program loads, and from
/// where: the copies it carries, or the system's. The first thing to run on a
/// new platform.
internal static class NativesCommand
{
    public static Task<int> RunAsync(string[] args)
    {
        if (SecureReader.OwnReader)
        {
            // No libcdio or libcdio-paranoia in the Mac build since 0.7.2, nor the Linux one since 0.7.3 (spec §11, steps 3 and 4).
            Console.WriteLine($"  libraries       none: Deadwax reads the drive with its own code on {(OperatingSystem.IsMacOS() ? "macOS" : "Linux")}");
            Console.WriteLine($"  engine          {SecureReader.EngineDescription}");
            Console.WriteLine($"  default device  {CdDrive.DefaultDevice}");
            return Task.FromResult(0);
        }
        foreach (var name in new[] { "cdio", "cdio_cdda", "cdio_paranoia" })
        {
            var handle = NativeLibraries.Load(name);
            Console.WriteLine(handle == IntPtr.Zero
                ? $"  {name,-14} NOT FOUND (looked for {string.Join(", ", NativeLibraries.Candidates(name))})"
                : $"  {name,-14} {NativeLibraries.Loaded.GetValueOrDefault(name, "loaded")}");
        }
        Console.WriteLine($"  engine          {CdDrive.EngineDescription}");
        Console.WriteLine($"  bundled         {(NativeLibraries.IsBundled ? "yes, the program's own copies" : "no, the system's")}");
        Console.WriteLine($"  default device  {CdDrive.DefaultDevice}");
        return Task.FromResult(0);
    }
}
