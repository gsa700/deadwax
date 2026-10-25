using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Deadwax.Drive;

/// Windows: MMC commands sent to the drive through SCSI pass-through
/// (IOCTL_SCSI_PASS_THROUGH_DIRECT) on \\.\D:, the same command bytes
/// libcdio's mmc_run_cmd sent until 0.7.4, which did the same
/// underneath (step 3 of the plan to need no native libraries, spec §11).
public sealed unsafe class WindowsSptiTransport : PassThroughTransport
{
    private const uint ScsiPassThroughDirect = 0x4D014;   // IOCTL_SCSI_PASS_THROUGH_DIRECT
    private const uint StorageEjectMedia = 0x2D4808;      // IOCTL_STORAGE_EJECT_MEDIA
    private const byte DataIn = 1, NoData = 2;            // SCSI_IOCTL_DATA_IN, _UNSPECIFIED
    private const uint TimeoutSeconds = 30;
    private const int HeaderBytes = 56, SenseBytes = 32;  // SCSI_PASS_THROUGH_DIRECT on 64-bit Windows, then the sense data
    private const int Alignment = 64;                     // more than any adapter's AlignmentMask asks

    private readonly SafeFileHandle _handle;

    /// "D:", "D:\" or "\\.\D:".
    public WindowsSptiTransport(string device) : base(device)
    {
        _handle = CreateFileW(PathOf(device), 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);   // read+write, share read+write, OPEN_EXISTING
        if (_handle.IsInvalid) throw new DriveException($"Could not open {device} (error {Marshal.GetLastPInvokeError()}).");
    }

    public static string PathOf(string device)
    {
        var d = device.TrimEnd('\\');
        return d.StartsWith(@"\\.\", StringComparison.Ordinal) ? d : $@"\\.\{d}";
    }

    protected override bool Run(ReadOnlySpan<byte> cdb, byte* data, int length)
    {
        var spt = stackalloc byte[HeaderBytes + SenseBytes];
        new Span<byte>(spt, HeaderBytes + SenseBytes).Clear();
        // The drive's data goes to a buffer of our own, aligned, and is copied out.
        var buffer = length == 0 ? null : (byte*)NativeMemory.AlignedAlloc((nuint)length, Alignment);
        try
        {
            *(ushort*)(spt + 0) = HeaderBytes;                 // Length
            spt[6] = (byte)cdb.Length;                         // CdbLength
            spt[7] = SenseBytes;                               // SenseInfoLength
            spt[8] = length == 0 ? NoData : DataIn;            // DataIn
            *(uint*)(spt + 12) = (uint)length;                 // DataTransferLength
            *(uint*)(spt + 16) = TimeoutSeconds;               // TimeOutValue
            *(void**)(spt + 24) = buffer;                      // DataBuffer
            *(uint*)(spt + 32) = HeaderBytes;                  // SenseInfoOffset
            cdb.CopyTo(new Span<byte>(spt + 36, 16));          // Cdb
            if (!DeviceIoControl(_handle, ScsiPassThroughDirect, spt, HeaderBytes + SenseBytes, spt, HeaderBytes + SenseBytes, out _, IntPtr.Zero))
                return false;
            // ScsiStatus zero, and every byte asked for delivered: Windows
            // leaves DataTransferLength at what the drive actually sent.
            if (spt[2] != 0 || *(uint*)(spt + 12) != (uint)length) return false;
            if (length > 0) new Span<byte>(buffer, length).CopyTo(new Span<byte>(data, length));
            return true;
        }
        finally
        {
            if (buffer is not null) NativeMemory.AlignedFree(buffer);
        }
    }

    /// Opens the tray (IOCTL_STORAGE_EJECT_MEDIA).
    public override bool Eject() => DeviceIoControl(_handle, StorageEjectMedia, null, 0, null, 0, out _, IntPtr.Zero);

    public override void Dispose() => _handle.Dispose();

    /// The CD drives Windows knows, as drive letters ("D:").
    public static IReadOnlyList<string> Drives()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom)
                .Select(d => d.Name.TrimEnd('\\')).ToList();
        }
        catch (IOException) { return []; }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, void* inBuffer, int inSize, void* outBuffer, int outSize, out int returned, IntPtr overlapped);
}
