using System.Reflection;
using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// The slice of libcdio Deadwax calls. Signatures follow libcdio 2.x (soname
/// libcdio.so.19); none of the -devel headers are needed to build.
///
/// Not here: libcdio's catalog and ISRC calls. They return nothing on the
/// BDR-209D for discs that have both, so those come from cdrdao (CdrdaoToc).
internal static partial class LibCdio
{
    private const string Lib = "cdio";

    // cdio/track.h
    public const byte LeadoutTrack = 0xAA;
    public const byte InvalidTrack = 0xFF;
    public const int InvalidLsn = -45301;
    public const int TrackFormatAudio = 0;

    // cdio/mmc_hl_cmds.h, cdio_hwinfo_t: three fixed char arrays, each with room for a NUL.
    public const int HwVendorLength = 8;
    public const int HwModelLength = 16;
    public const int HwRevisionLength = 4;
    public const int HwInfoSize = HwVendorLength + 1 + HwModelLength + 1 + HwRevisionLength + 1;

    /// Opens a CD device with whatever driver fits it. Null when there is no
    /// device, or no disc in it.
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr cdio_open_cd(string? source);

    [LibraryImport(Lib)]
    public static partial void cdio_destroy(IntPtr cdio);

    [LibraryImport(Lib)]
    public static partial byte cdio_get_first_track_num(IntPtr cdio);

    [LibraryImport(Lib)]
    public static partial byte cdio_get_num_tracks(IntPtr cdio);

    [LibraryImport(Lib)]
    public static partial int cdio_get_track_lsn(IntPtr cdio, byte track);

    [LibraryImport(Lib)]
    public static partial int cdio_get_track_format(IntPtr cdio, byte track);

    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static unsafe partial bool cdio_get_hwinfo(IntPtr cdio, byte* hwinfo);

    // Distributions ship libcdio as libcdio.so.19 and only install the bare
    // libcdio.so with the -devel package, so the plain name would not load on a
    // machine that only has the library. Try the soname first. An explicit static
    // constructor runs before the first call into this class, so before any load.
    static LibCdio() => NativeLibrary.SetDllImportResolver(typeof(LibCdio).Assembly, Resolve);

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;
        foreach (var candidate in new[] { "libcdio.so.19", "libcdio.so" })
        {
            if (!NativeLibrary.TryLoad(candidate, assembly, path, out var handle)) continue;
            Quiet(handle);
            return handle;
        }
        return IntPtr.Zero;
    }

    // cdio_log_level_t: DEBUG = 1, INFO, WARN, ERROR, ASSERT.
    private const int LogError = 4;

    /// libcdio prints its own warnings to stderr ("++ WARN: error in ioctl
    /// CDROMREADTOCHDR: No medium found") on top of the error Deadwax reports for
    /// the same thing. Raise its threshold to errors only, through the exported
    /// global it reads, before the first call can print anything.
    private static void Quiet(IntPtr handle)
    {
        if (NativeLibrary.TryGetExport(handle, "cdio_loglevel_default", out var level))
            Marshal.WriteInt32(level, LogError);
    }
}
