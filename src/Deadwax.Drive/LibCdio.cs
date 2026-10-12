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

    /// cdio/device.h: caps the drive's read speed, in multiples of 1x (176 kB/s),
    /// MMC SET CD SPEED underneath. 0 = success. The drive keeps the cap until
    /// the disc is changed or it is reset.
    [LibraryImport(Lib)]
    public static partial int cdio_set_speed(IntPtr cdio, int speed);

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

    // cdio/device.h, driver_id_t: the last member, "any real drive on this system".
    public const int DriverDevice = 11;

    /// The CD drives libcdio can find, as a NULL-terminated array of C strings;
    /// free with cdio_free_device_list. Null when there are none.
    [LibraryImport(Lib)]
    public static partial IntPtr cdio_get_devices(int driverId);

    [LibraryImport(Lib)]
    public static partial void cdio_free_device_list(IntPtr list);

    /// The platform's default CD device as a C string the caller frees with
    /// cdio_free; p_cdio may be NULL.
    [LibraryImport(Lib)]
    public static partial IntPtr cdio_get_default_device(IntPtr cdio);

    [LibraryImport(Lib)]
    public static partial void cdio_free(IntPtr memory);

    /// Ejects by device name without holding the device open (MMC START STOP
    /// UNIT, or the platform's ioctl). 0 = DRIVER_OP_SUCCESS.
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cdio_eject_media_drive(string device);

    /// The drives libcdio can see on this machine, in its order.
    public static unsafe IReadOnlyList<string> Devices()
    {
        var list = cdio_get_devices(DriverDevice);
        if (list == IntPtr.Zero) return [];
        try
        {
            var result = new List<string>();
            for (var p = (IntPtr*)list; *p != IntPtr.Zero; p++)
                if (Marshal.PtrToStringUTF8(*p) is { Length: > 0 } s) result.Add(s);
            return result;
        }
        finally { cdio_free_device_list(list); }
    }

    public static string? DefaultDevice()
    {
        var p = cdio_get_default_device(IntPtr.Zero);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { cdio_free(p); }
    }

    // cdio/track.h, track_flag_t
    public const int TrackFlagTrue = 1;

    [LibraryImport(Lib)]
    public static partial int cdio_get_track_preemphasis(IntPtr cdio, byte track);

    [LibraryImport(Lib)]
    public static partial int cdio_get_track_copy_permit(IntPtr cdio, byte track);

    /// 2 or 4; -1 when unknown.
    [LibraryImport(Lib)]
    public static partial int cdio_get_track_channels(IntPtr cdio, byte track);

    // cdio/mmc.h: cdio_mmc_direction_t
    public const int MmcDataRead = 0;

    /// Sends one MMC command through the platform's pass-through (SG_IO on
    /// Linux, SCSITask on macOS, SPTI on Windows). The CDB is a 16-byte
    /// mmc_cdb_t; its real length follows from the opcode. 0 = success.
    [LibraryImport(Lib)]
    public static unsafe partial int mmc_run_cmd(IntPtr cdio, uint timeoutMs, byte* cdb, int direction, uint bufLen, byte* buf);

    static LibCdio() => NativeLibraries.Register();

    /// libcdio's version, from its exported `const char *cdio_version_string`,
    /// for the rip log ("2.3.0"); null if the library cannot be loaded.
    public static string? Version()
    {
        NativeLibraries.Register();
        var handle = NativeLibraries.Load(Lib);
        if (handle == IntPtr.Zero) return null;
        if (!NativeLibrary.TryGetExport(handle, "cdio_version_string", out var slot)) return null;
        return Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(slot));
    }

    // cdio_log_level_t: DEBUG = 1, INFO, WARN, ERROR, ASSERT.
    private const int LogError = 4;

    /// libcdio prints its own warnings to stderr ("++ WARN: error in ioctl
    /// CDROMREADTOCHDR: No medium found") on top of the error Deadwax reports for
    /// the same thing. Raise its threshold to errors only, through the exported
    /// global it reads, before the first call can print anything.
    internal static void Quiet(IntPtr handle)
    {
        if (NativeLibrary.TryGetExport(handle, "cdio_loglevel_default", out var level))
            Marshal.WriteInt32(level, LogError);
    }
}
