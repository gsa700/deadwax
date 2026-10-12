using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// The three things the subchannel reader asks a drive for, behind whatever
/// each platform offers. Linux and Windows: libcdio's MMC pass-through
/// (SG_IO, SPTI). macOS: libcdio's pass-through is compiled out there
/// (security scan 2026-10-12, L2: `GET_SCSI_FIXED` is never defined), so
/// the CD ioctls of the raw disk device do the same work, and need neither
/// exclusive access nor an unmounted disc.
public interface IMmcTransport : IDisposable
{
    /// Audio plus subchannel for `count` sectors from `lsn`: 2352 bytes of
    /// audio then 16 bytes of formatted Q per sector, or 96 bytes of raw
    /// P-W when `rawSubchannel`. Null when the command failed.
    byte[]? ReadCd(int lsn, int count, bool rawSubchannel);

    /// Whether raw P-W can be asked for at all.
    bool SupportsRawSubchannel { get; }

    /// READ SUB-CHANNEL: the MCN (13 digits) or a track's ISRC (12 characters),
    /// as recorded; null when the drive has none or the command is unsupported.
    string? ReadCode(bool isrc, byte track);

    /// The CD-Text packs (18 bytes each), without the four-byte header; null
    /// when there is no CD-Text.
    byte[]? ReadCdTextPacks();
}

/// libcdio's mmc_run_cmd, with the CDBs Deadwax builds itself.
public sealed unsafe class LibCdioTransport(IntPtr handle) : IMmcTransport
{
    private const uint TimeoutMs = 30_000;

    public bool SupportsRawSubchannel => true;

    public byte[]? ReadCd(int lsn, int count, bool rawSubchannel)
    {
        var block = 2352 + (rawSubchannel ? 96 : 16);
        var buffer = new byte[count * block];
        var cdb = stackalloc byte[16];
        cdb[0] = 0xBE;
        cdb[2] = (byte)(lsn >> 24); cdb[3] = (byte)(lsn >> 16); cdb[4] = (byte)(lsn >> 8); cdb[5] = (byte)lsn;
        cdb[6] = (byte)(count >> 16); cdb[7] = (byte)(count >> 8); cdb[8] = (byte)count;
        cdb[9] = 0xF8;
        cdb[10] = rawSubchannel ? (byte)0x01 : (byte)0x02;
        var rc = -1;
        for (var attempt = 0; attempt < 6 && rc != 0; attempt++)
            fixed (byte* buf = buffer) rc = LibCdio.mmc_run_cmd(handle, TimeoutMs, cdb, LibCdio.MmcDataRead, (uint)buffer.Length, buf);
        return rc == 0 ? buffer : null;
    }

    public string? ReadCode(bool isrc, byte track)
    {
        var cdb = stackalloc byte[16];
        cdb[0] = 0x42;
        cdb[2] = 0x40;
        cdb[3] = isrc ? (byte)0x03 : (byte)0x02;
        cdb[6] = track;
        cdb[8] = 24;
        var data = stackalloc byte[24];
        if (LibCdio.mmc_run_cmd(handle, TimeoutMs, cdb, LibCdio.MmcDataRead, 24, data) != 0) return null;
        if ((data[8] & 0x80) == 0) return null;
        var length = isrc ? 12 : 13;
        var code = System.Text.Encoding.ASCII.GetString(data + 9, length).TrimEnd('\0');
        return code.Length == length ? code : null;
    }

    public byte[]? ReadCdTextPacks()
    {
        var cdb = stackalloc byte[16];
        cdb[0] = 0x43;
        cdb[2] = 0x05;
        cdb[8] = 4;
        var head = stackalloc byte[4];
        if (LibCdio.mmc_run_cmd(handle, TimeoutMs, cdb, LibCdio.MmcDataRead, 4, head) != 0) return null;
        var length = (head[0] << 8 | head[1]) + 2;
        if (length <= 4 || length > 36_864 + 4) return null;
        var buffer = new byte[length];
        cdb[7] = (byte)(length >> 8); cdb[8] = (byte)length;
        fixed (byte* buf = buffer)
            if (LibCdio.mmc_run_cmd(handle, TimeoutMs, cdb, LibCdio.MmcDataRead, (uint)length, buf) != 0) return null;
        return CdTextPacks.FromResponse(buffer);
    }

    public void Dispose() { }
}

/// macOS: the CD ioctls of /dev/rdiskN (IOCDMediaBSDClient.h). Offsets are
/// in bytes of 2352-byte sectors; "sector area" bits say what each sector
/// should include. Untested until a Mac is here (2026-10-14).
public sealed unsafe class MacIoctlTransport : IMmcTransport
{
    // _IOWR('d', n, size): IOC_INOUT | (size << 16) | ('d' << 8) | n, on LP64.
    private const uint ReadIoctl = 0xC0206460;      // DKIOCCDREAD, dk_cd_read_t (32 bytes)
    private const uint IsrcIoctl = 0xC0106461;      // DKIOCCDREADISRC, dk_cd_read_isrc_t (16 bytes)
    private const uint McnIoctl = 0xC0106462;       // DKIOCCDREADMCN, dk_cd_read_mcn_t (16 bytes)
    private const uint SetSpeedIoctl = 0x80026463;  // DKIOCCDSETSPEED, uint16_t (KB/s)
    private const uint TocIoctl = 0xC0186464;       // DKIOCCDREADTOC, dk_cd_read_toc_t (24 bytes)
    private const byte AreaUser = 0x10, AreaSubChannelQ = 0x04, TypeCdda = 0x01, FormatText = 0x05;
    private const int SectorBytes = 2352, QBytes = 16;

    private int _fd;

    public MacIoctlTransport(string device)
    {
        // The raw device, whatever the caller named: /dev/disk2 -> /dev/rdisk2.
        var name = device.StartsWith("/dev/disk", StringComparison.Ordinal) ? "/dev/r" + device[5..] : device;
        _fd = open(name, 0x0004 | 0x0000); // O_NONBLOCK | O_RDONLY
        if (_fd < 0) throw new DriveException($"Could not open {name} (errno {Marshal.GetLastPInvokeError()}).");
    }

    public bool SupportsRawSubchannel => false;

    public byte[]? ReadCd(int lsn, int count, bool rawSubchannel)
    {
        if (rawSubchannel) return null;
        var buffer = new byte[count * (SectorBytes + QBytes)];
        var req = stackalloc byte[32];
        *(ulong*)(req + 0) = (ulong)lsn * SectorBytes;
        req[8] = AreaUser | AreaSubChannelQ;
        req[9] = TypeCdda;
        *(uint*)(req + 20) = (uint)buffer.Length;
        fixed (byte* buf = buffer)
        {
            *(void**)(req + 24) = buf;
            if (ioctl(_fd, ReadIoctl, req) != 0) return null;
        }
        return *(uint*)(req + 20) == (uint)buffer.Length ? buffer : null;
    }

    public string? ReadCode(bool isrc, byte track)
    {
        var req = stackalloc byte[16];
        if (isrc) req[13] = track;
        if (ioctl(_fd, isrc ? IsrcIoctl : McnIoctl, req) != 0) return null;
        var length = isrc ? 12 : 13;
        var code = Marshal.PtrToStringUTF8((IntPtr)req, length)?.TrimEnd('\0');
        return code?.Length == length && code.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z') ? code : null;
    }

    public byte[]? ReadCdTextPacks()
    {
        var buffer = new byte[36_864 + 4];
        var req = stackalloc byte[24];
        req[0] = FormatText;
        *(ushort*)(req + 14) = (ushort)buffer.Length;
        fixed (byte* buf = buffer)
        {
            *(void**)(req + 16) = buf;
            if (ioctl(_fd, TocIoctl, req) != 0) return null;
        }
        var got = *(ushort*)(req + 14);
        return got <= 4 ? null : CdTextPacks.FromResponse(buffer.AsSpan(0, got).ToArray());
    }

    /// DKIOCCDSETSPEED takes KB/s: 176 per 1x. 0xFFFF = the drive's choice.
    public bool SetSpeed(int? multiple)
    {
        var kbps = (ushort)(multiple is { } m ? Math.Min(65535, m * 176) : 0xFFFF);
        return ioctl(_fd, SetSpeedIoctl, &kbps) == 0;
    }

    public void Dispose()
    {
        if (_fd >= 0) { close(_fd); _fd = -1; }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, uint request, void* arg);
    [DllImport("libc")] private static extern int close(int fd);
}
