using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// What Deadwax's own drive code asks of a drive, whatever the platform: the
/// subchannel reader's commands (IMmcTransport), audio with C2 flags for
/// PassReader, the full TOC, and the speed. MacIoctlTransport on macOS,
/// LinuxSgTransport on Linux, WindowsSptiTransport on Windows.
public interface IDriveTransport : IMmcTransport
{
    /// See MacIoctlTransport.ReadAudio.
    byte[]? ReadAudio(int lsn, int count, bool c2);

    /// READ TOC format 2, as the drive sent it (FullToc reads it).
    byte[]? ReadFullToc();

    /// Caps the read speed at `multiple` times 1x; null lifts the cap.
    bool SetSpeed(int? multiple);

    /// The platform's own transport for a device.
    public static IDriveTransport Open(string device) =>
        OperatingSystem.IsMacOS() ? new MacIoctlTransport(device)
        : OperatingSystem.IsLinux() ? new LinuxSgTransport(device)
        : OperatingSystem.IsWindows() ? new WindowsSptiTransport(device)
        : throw new DriveException("Deadwax's own drive access is not written for this system yet.");

    /// How the platform's own transport reaches the drive, for the rip log.
    public static string Kind => OperatingSystem.IsMacOS() ? "DKIOCCDREAD" : OperatingSystem.IsWindows() ? "SPTI" : "SG_IO";
}

/// Linux: MMC commands sent to the drive through the SCSI generic ioctl
/// (SG_IO) on /dev/srN, the same command bytes libcdio's mmc_run_cmd sent
/// until 0.7.3, which did the same underneath (step 3 of the plan
/// to need no native libraries, spec §11).
public sealed unsafe class LinuxSgTransport : PassThroughTransport
{
    private const uint SgIo = 0x2285;
    private const uint CdromEject = 0x5309;
    private const int FromDevice = -3;   // SG_DXFER_FROM_DEV
    private const int NoData = -1;       // SG_DXFER_NONE
    private const uint TimeoutMs = 30_000;

    private int _fd;

    public LinuxSgTransport(string device) : base(device)
    {
        _fd = open(device, 0x800);   // O_RDONLY | O_NONBLOCK: an open needs no disc to be ready
        if (_fd < 0) throw new DriveException($"Could not open {device} (errno {Marshal.GetLastPInvokeError()}).");
    }

    protected override bool Run(ReadOnlySpan<byte> cdb, byte* data, int length)
    {
        // struct sg_io_hdr, x86-64 and arm64 Linux (88 bytes).
        var hdr = stackalloc byte[88];
        new Span<byte>(hdr, 88).Clear();
        var sense = stackalloc byte[32];
        var command = stackalloc byte[16];
        cdb.CopyTo(new Span<byte>(command, 16));
        *(int*)(hdr + 0) = 'S';                       // interface_id
        *(int*)(hdr + 4) = length == 0 ? NoData : FromDevice;
        hdr[8] = (byte)cdb.Length;                    // cmd_len
        hdr[9] = 32;                                  // mx_sb_len
        *(uint*)(hdr + 12) = (uint)length;            // dxfer_len
        *(void**)(hdr + 16) = data;                   // dxferp
        *(void**)(hdr + 24) = command;                // cmdp
        *(void**)(hdr + 32) = sense;                  // sbp
        *(uint*)(hdr + 40) = TimeoutMs;               // timeout
        if (ioctl(_fd, SgIo, hdr) != 0) return false;
        // status, host_status and driver_status all zero, and every byte asked
        // for delivered (resid 0); see Run in PassThroughTransport. The Mac's
        // ioctl reports what it delivered and was always checked.
        return hdr[64] == 0 && *(ushort*)(hdr + 68) == 0 && *(ushort*)(hdr + 70) == 0 && *(int*)(hdr + 72) == 0;
    }

    /// Opens the tray (the kernel's CDROMEJECT, which also unlocks it).
    public override bool Eject() => ioctl(_fd, CdromEject, null) == 0;

    public override void Dispose()
    {
        if (_fd >= 0) { close(_fd); _fd = -1; }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, void* arg);
    [DllImport("libc")] private static extern int close(int fd);
}
