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

    /// Directories tried after the program's own: on macOS the Homebrew prefix
    /// for this architecture, which the dynamic loader does not search.
    private static IEnumerable<string> ExtraDirectories()
    {
        if (!OperatingSystem.IsMacOS()) yield break;
        yield return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "/opt/homebrew/lib" : "/usr/local/lib";
    }

    /// What was loaded for each base name, for `deadwax natives` and the log.
    public static IReadOnlyDictionary<string, string> Loaded => _loaded;
    private static readonly Dictionary<string, string> _loaded = [];

    /// True when the copy the program carries was loaded, for any of the libraries.
    public static bool IsBundled { get; private set; }

    /// Loads a library by its base name, or returns IntPtr.Zero.
    ///
    /// OURS FIRST, THE SYSTEM'S SECOND, as AlbumWall loads its engine: asked
    /// with the assembly and AssemblyDirectory, the runtime probes the
    /// program's own folders, including wherever a single-file build unpacked
    /// its native libraries; a bare TryLoad is a plain dlopen and on Linux
    /// never looks there. A checkout without native/ falls through to the
    /// distribution's copy, as it always did.
    ///
    /// The paranoia libraries need libcdio, and libcdio_paranoia needs
    /// libcdio_cdda, by soname. Loading the dependencies first means the
    /// loader finds the bundled copies already in the process rather than
    /// going looking on the system for them.
    public static IntPtr Load(string name, Assembly? assembly = null, DllImportSearchPath? path = null)
    {
        if (name == "cdio_paranoia") Load("cdio_cdda", assembly, path);
        if (name is "cdio_cdda" or "cdio_paranoia") Load("cdio", assembly, path);
        assembly ??= typeof(NativeLibraries).Assembly;
        var search = path ?? DllImportSearchPath.AssemblyDirectory;
        foreach (var candidate in Candidates(name))
        {
            if (NativeLibrary.TryLoad(candidate, assembly, search, out var ours))
            {
                var from = PathOf(candidate);
                var bundled = from is not null && IsOurs(from);
                if (bundled) IsBundled = true;
                _loaded[name] = from is null ? candidate : bundled ? $"{from} (bundled)" : from;
                return ours;
            }
        }
        foreach (var candidate in Candidates(name))
        {
            foreach (var dir in ExtraDirectories())
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out var h)) { _loaded[name] = full; return h; }
            }
            if (NativeLibrary.TryLoad(candidate, out var handle)) { _loaded[name] = PathOf(candidate) ?? candidate; return handle; }
        }
        return IntPtr.Zero;
    }

    /// Inside the program's folder or the folder a single-file build unpacked into.
    private static bool IsOurs(string file)
    {
        var app = AppContext.BaseDirectory;
        if (file.StartsWith(app, StringComparison.Ordinal)) return true;
        var extract = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        if (!string.IsNullOrEmpty(extract) && file.StartsWith(extract, StringComparison.Ordinal)) return true;
        // The default extraction folder: $TMPDIR/.net/<user>/ on Unix, %TEMP%\.net on Windows.
        return file.Contains($"{Path.DirectorySeparatorChar}.net{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    /// The file a loaded library came from: /proc/self/maps on Linux, the
    /// dyld image list on macOS, the module's file name on Windows. Null if
    /// it cannot be told.
    public static string? PathOf(string fileName)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var line in File.ReadLines("/proc/self/maps"))
                {
                    var at = line.IndexOf('/');
                    if (at < 0) continue;
                    var p = line[at..];
                    if (Path.GetFileName(p) == fileName) return p;
                }
                return null;
            }
            if (OperatingSystem.IsMacOS())
            {
                var n = _dyld_image_count();
                for (uint i = 0; i < n; i++)
                {
                    var p = Marshal.PtrToStringUTF8(_dyld_get_image_name(i));
                    if (p is not null && Path.GetFileName(p) == fileName) return p;
                }
                return null;
            }
            if (OperatingSystem.IsWindows())
            {
                var module = GetModuleHandleW(fileName);
                if (module == IntPtr.Zero) return null;
                var buffer = new char[1024];
                var len = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
                return len == 0 ? null : new string(buffer, 0, (int)len);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    [DllImport("libSystem.B.dylib")] private static extern uint _dyld_image_count();
    [DllImport("libSystem.B.dylib")] private static extern IntPtr _dyld_get_image_name(uint index);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] buffer, uint size);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        var handle = Load(name, assembly, path);
        if (handle != IntPtr.Zero && name == "cdio") LibCdio.Quiet(handle);
        return handle;
    }
}
