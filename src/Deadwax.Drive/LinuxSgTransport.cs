using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// What Deadwax's own drive code asks of a drive, whatever the platform: the
/// subchannel reader's commands (IMmcTransport), audio with C2 flags for
/// PassReader, the full TOC, and the speed. MacIoctlTransport on macOS,
/// LinuxSgTransport on Linux; Windows (SPTI) to come.
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
        : throw new DriveException("Deadwax's own drive access is not written for this system yet.");
}

/// Linux: MMC commands sent to the drive through the SCSI generic ioctl
/// (SG_IO) on /dev/srN, the same command bytes LibCdioTransport hands to
/// libcdio's mmc_run_cmd, which does the same underneath (step 3 of the plan
/// to need no native libraries, spec §11).
public sealed unsafe class LinuxSgTransport : IDriveTransport
{
    private const uint SgIo = 0x2285;
    private const uint CdromEject = 0x5309;
    private const int FromDevice = -3;   // SG_DXFER_FROM_DEV
    private const int NoData = -1;       // SG_DXFER_NONE
    private const uint TimeoutMs = 30_000;
    private const int SectorBytes = 2352, C2Bytes = 294;

    private int _fd;

    public string Device { get; }

    public LinuxSgTransport(string device)
    {
        Device = device;
        _fd = open(device, 0x800);   // O_RDONLY | O_NONBLOCK: an open needs no disc to be ready
        if (_fd < 0) throw new DriveException($"Could not open {device} (errno {Marshal.GetLastPInvokeError()}).");
    }

    /// One command. True when the drive took it and answered without error.
    private bool Run(ReadOnlySpan<byte> cdb, byte* data, int length, int direction = FromDevice)
    {
        // struct sg_io_hdr, x86-64 and arm64 Linux (88 bytes).
        var hdr = stackalloc byte[88];
        new Span<byte>(hdr, 88).Clear();
        var sense = stackalloc byte[32];
        var command = stackalloc byte[16];
        cdb.CopyTo(new Span<byte>(command, 16));
        *(int*)(hdr + 0) = 'S';                       // interface_id
        *(int*)(hdr + 4) = length == 0 ? NoData : direction;
        hdr[8] = (byte)cdb.Length;                    // cmd_len
        hdr[9] = 32;                                  // mx_sb_len
        *(uint*)(hdr + 12) = (uint)length;            // dxfer_len
        *(void**)(hdr + 16) = data;                   // dxferp
        *(void**)(hdr + 24) = command;                // cmdp
        *(void**)(hdr + 32) = sense;                  // sbp
        *(uint*)(hdr + 40) = TimeoutMs;               // timeout
        if (ioctl(_fd, SgIo, hdr) != 0) return false;
        // status, host_status and driver_status all zero, and every byte asked
        // for delivered (resid 0). A drive can end a READ CD early, at a track
        // start, with a good status: the BDR-209D on Techbench did, and the
        // missing part stayed zeros that read as clean silence, the same in
        // both passes, so wrong tracks were called Copy OK (2026-10-24). The
        // Mac's ioctl reports what it delivered and was always checked.
        return hdr[64] == 0 && *(ushort*)(hdr + 68) == 0 && *(ushort*)(hdr + 70) == 0 && *(int*)(hdr + 72) == 0;
    }

    public bool SupportsRawSubchannel => true;

    public byte[]? ReadCd(int lsn, int count, bool rawSubchannel)
    {
        var block = SectorBytes + (rawSubchannel ? 96 : 16);
        var buffer = new byte[count * block];
        Span<byte> cdb = stackalloc byte[12];
        ReadCdCdb(cdb, lsn, count);
        cdb[9] = 0xF8;
        cdb[10] = rawSubchannel ? (byte)0x01 : (byte)0x02;
        for (var attempt = 0; attempt < 6; attempt++)
            fixed (byte* buf = buffer)
                if (Run(cdb, buf, buffer.Length)) return buffer;
        return null;
    }

    public byte[]? ReadAudio(int lsn, int count, bool c2)
    {
        var buffer = new byte[count * (SectorBytes + (c2 ? C2Bytes : 0))];
        Span<byte> cdb = stackalloc byte[12];
        ReadCdCdb(cdb, lsn, count);
        cdb[9] = (byte)(0xF8 | (c2 ? 0x02 : 0x00));   // the whole sector, and with c2 the 294 bytes of C2 error flags
        fixed (byte* buf = buffer) return Run(cdb, buf, buffer.Length) ? buffer : null;
    }

    private static void ReadCdCdb(Span<byte> cdb, int lsn, int count)
    {
        cdb[0] = 0xBE;   // READ CD
        cdb[2] = (byte)(lsn >> 24); cdb[3] = (byte)(lsn >> 16); cdb[4] = (byte)(lsn >> 8); cdb[5] = (byte)lsn;
        cdb[6] = (byte)(count >> 16); cdb[7] = (byte)(count >> 8); cdb[8] = (byte)count;
    }

    public string? ReadCode(bool isrc, byte track)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = 0x42;   // READ SUB-CHANNEL
        cdb[2] = 0x40;
        cdb[3] = isrc ? (byte)0x03 : (byte)0x02;
        cdb[6] = track;
        cdb[8] = 24;
        var data = stackalloc byte[24];
        if (!Run(cdb, data, 24)) return null;
        if ((data[8] & 0x80) == 0) return null;
        var length = isrc ? 12 : 13;
        var code = System.Text.Encoding.ASCII.GetString(data + 9, length).TrimEnd('\0');
        return code.Length == length ? code : null;
    }

    public byte[]? ReadCdTextPacks() => ReadToc(0x05, msf: false) is { } r && r.Length > 4 ? CdTextPacks.FromResponse(r) : null;

    public byte[]? ReadFullToc() => ReadToc(0x02, msf: true);

    /// READ TOC/PMA/ATIP in the given format: its length first, then all of it.
    private byte[]? ReadToc(byte format, bool msf)
    {
        Span<byte> cdb = stackalloc byte[10];
        cdb[0] = 0x43;
        cdb[1] = msf ? (byte)0x02 : (byte)0x00;
        cdb[2] = format;
        cdb[6] = format == 0x02 ? (byte)1 : (byte)0;   // the full TOC: from session 1
        cdb[8] = 4;
        var head = stackalloc byte[4];
        if (!Run(cdb, head, 4)) return null;
        var length = (head[0] << 8 | head[1]) + 2;
        if (length < 4 || length > 36_864 + 4) return null;
        var buffer = new byte[length];
        cdb[7] = (byte)(length >> 8); cdb[8] = (byte)length;
        fixed (byte* buf = buffer) return Run(cdb, buf, length) ? buffer : null;
    }

    public bool SetSpeed(int? multiple)
    {
        var kbps = multiple is { } m ? Math.Min(65535, m * 176) : 0xFFFF;
        Span<byte> cdb = stackalloc byte[12];
        cdb[0] = 0xBB;   // SET CD SPEED
        cdb[2] = (byte)(kbps >> 8); cdb[3] = (byte)kbps;
        cdb[4] = 0xFF; cdb[5] = 0xFF;   // write speed: the drive's own
        return Run(cdb, null, 0);
    }

    /// Opens the tray (the kernel's CDROMEJECT, which also unlocks it).
    public bool Eject() => ioctl(_fd, CdromEject, null) == 0;

    public void Dispose()
    {
        if (_fd >= 0) { close(_fd); _fd = -1; }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint request, void* arg);
    [DllImport("libc")] private static extern int close(int fd);
}
