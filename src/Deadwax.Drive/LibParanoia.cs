using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// libcdio-paranoia: the cdda layer (libcdio_cdda.so.2) that talks to the drive,
/// and paranoia itself (libcdio_paranoia.so.2) that reads, compares overlapping
/// reads and repairs. Same engine whipper drove through the cd-paranoia binary
/// ("cdparanoia III 10.2 libcdio 2.3.0" in every log).
internal static unsafe partial class LibParanoia
{
    private const string Cdda = "cdio_cdda";
    private const string Paranoia = "cdio_paranoia";

    public const int SectorBytes = 2352;
    public const int SectorWords = SectorBytes / 2;   // what the callback counts in
    public const int SamplesPerSector = SectorBytes / 4;

    // cdda_interface.h: where library messages go.
    public const int MessageForgetIt = 0;

    // paranoia.h, paranoia_mode_t
    public const int ModeFull = 0xFF;
    public const int ModeNeverSkip = 0x20;

    public const int SeekSet = 0;

    [LibraryImport(Cdda, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr cdio_cddap_identify(string device, int messageDest, IntPtr messages);

    [LibraryImport(Cdda)]
    public static partial int cdio_cddap_open(IntPtr drive);

    [LibraryImport(Cdda)]
    public static partial int cdio_cddap_close(IntPtr drive);

    [LibraryImport(Paranoia)]
    public static partial IntPtr cdio_paranoia_init(IntPtr drive);

    [LibraryImport(Paranoia)]
    public static partial void cdio_paranoia_free(IntPtr paranoia);

    [LibraryImport(Paranoia)]
    public static partial void cdio_paranoia_modeset(IntPtr paranoia, int mode);

    [LibraryImport(Paranoia)]
    public static partial int cdio_paranoia_seek(IntPtr paranoia, int sector, int whence);

    /// One sector of audio, owned by paranoia and valid until the next read.
    /// Null on a fatal error.
    [LibraryImport(Paranoia)]
    public static partial short* cdio_paranoia_read_limited(
        IntPtr paranoia, delegate* unmanaged<CLong, int, void> callback, int maxRetries);

    static LibParanoia() => NativeLibraries.Register();
}
