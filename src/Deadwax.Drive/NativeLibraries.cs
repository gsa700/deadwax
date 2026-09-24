using System.Reflection;
using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// Finds libcdio and libcdio-paranoia by soname.
///
/// Distributions ship them as libcdio.so.19, libcdio_cdda.so.2 and
/// libcdio_paranoia.so.2, and only install the bare .so names with the -devel
/// packages, so the plain names would not load on a machine that only has the
/// libraries. .NET allows one resolver per assembly, hence one place for all.
internal static class NativeLibraries
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        string[] candidates = name switch
        {
            "cdio" => ["libcdio.so.19", "libcdio.so"],
            "cdio_cdda" => ["libcdio_cdda.so.2", "libcdio_cdda.so"],
            "cdio_paranoia" => ["libcdio_paranoia.so.2", "libcdio_paranoia.so"],
            _ => [],
        };
        foreach (var candidate in candidates)
        {
            if (!NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) continue;
            if (name == "cdio") LibCdio.Quiet(handle);
            return handle;
        }
        return IntPtr.Zero;
    }
}
