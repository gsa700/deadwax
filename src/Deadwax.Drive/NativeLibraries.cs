using System.Reflection;
using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// Finds libcdio and libcdio-paranoia under the name each platform gives them.
///
/// Linux distributions ship them as libcdio.so.19, libcdio_cdda.so.2 and
/// libcdio_paranoia.so.2, and only install the bare .so names with the -devel
/// packages, so the plain names would not load on a machine that only has the
/// libraries. Homebrew names the same libraries libcdio.19.dylib and so on,
/// and MSYS2's mingw builds libcdio-19.dll; vcpkg drops the version. One list
/// per platform, versioned name first. .NET allows one resolver per assembly,
/// hence one place for all.
///
/// Where they are looked for: next to the program first (a build that carries
/// its own copies, which is what a release will do off Linux), then the
/// platform's usual library paths, then Homebrew's prefix on macOS, which is
/// not on the dynamic loader's default path.
public static class NativeLibraries
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    /// The file names tried, in order, for a library known by its base name
    /// ("cdio", "cdio_cdda", "cdio_paranoia", "FLAC") on this platform.
    public static string[] Candidates(string name)
    {
        if (OperatingSystem.IsWindows())
            return name switch
            {
                "cdio" => ["libcdio-19.dll", "libcdio.dll", "cdio.dll"],
                "cdio_cdda" => ["libcdio_cdda-2.dll", "libcdio_cdda.dll", "cdio_cdda.dll"],
                "cdio_paranoia" => ["libcdio_paranoia-2.dll", "libcdio_paranoia.dll", "cdio_paranoia.dll"],
                "FLAC" => ["libFLAC-14.dll", "libFLAC-12.dll", "libFLAC.dll", "FLAC.dll"],
                _ => [],
            };
        if (OperatingSystem.IsMacOS())
            return name switch
            {
                "cdio" => ["libcdio.19.dylib", "libcdio.dylib"],
                "cdio_cdda" => ["libcdio_cdda.2.dylib", "libcdio_cdda.dylib"],
                "cdio_paranoia" => ["libcdio_paranoia.2.dylib", "libcdio_paranoia.dylib"],
                "FLAC" => ["libFLAC.14.dylib", "libFLAC.12.dylib", "libFLAC.dylib"],
                _ => [],
            };
        return name switch
        {
            "cdio" => ["libcdio.so.19", "libcdio.so"],
            "cdio_cdda" => ["libcdio_cdda.so.2", "libcdio_cdda.so"],
            "cdio_paranoia" => ["libcdio_paranoia.so.2", "libcdio_paranoia.so"],
            "FLAC" => ["libFLAC.so.14", "libFLAC.so.12", "libFLAC.so"],
            _ => [],
        };
    }

    /// The first candidate's name, for an error message ("looked for libcdio.so.19").
    public static string Expected(string name) => Candidates(name).FirstOrDefault() ?? name;

    /// Directories tried before the loader's own search: the program's own
    /// folder, and on macOS the Homebrew prefix for this architecture.
    private static IEnumerable<string> ExtraDirectories()
    {
        if (AppContext.BaseDirectory is { Length: > 0 } app) yield return app;
        if (!OperatingSystem.IsMacOS()) yield break;
        yield return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "/opt/homebrew/lib" : "/usr/local/lib";
    }

    /// Loads a library by its base name, or returns IntPtr.Zero.
    public static IntPtr Load(string name, Assembly? assembly = null, DllImportSearchPath? path = null)
    {
        foreach (var candidate in Candidates(name))
        {
            foreach (var dir in ExtraDirectories())
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out var h)) return h;
            }
            var ok = assembly is null
                ? NativeLibrary.TryLoad(candidate, out var handle)
                : NativeLibrary.TryLoad(candidate, assembly, path, out handle);
            if (ok) return handle;
        }
        return IntPtr.Zero;
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        var handle = Load(name, assembly, path);
        if (handle != IntPtr.Zero && name == "cdio") LibCdio.Quiet(handle);
        return handle;
    }
}
