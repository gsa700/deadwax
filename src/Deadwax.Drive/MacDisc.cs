using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// macOS mounts an audio CD the moment it is inserted (the Finder's "Audio
/// CD" of AIFF files), and while it is mounted the drive cannot be used for
/// MMC commands: libcdio's OS X driver asks IOKit for exclusive access and
/// is refused. Every ripper on the Mac unmounts the disc first (XLD, for
/// one), and so does Deadwax, through DiskArbitration, the same calls
/// libcdio's own eject uses. Unmount, not eject: the disc stays in.
///
/// Nothing here runs anywhere but macOS, and nothing is tested until it has
/// been (the first Mac arrives 2026-10-14).
public static unsafe class MacDisc
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string DiskArbitration = "/System/Library/Frameworks/DiskArbitration.framework/DiskArbitration";

    private const uint UnmountOptionWhole = 0x00000100;   // kDADiskUnmountOptionWhole: every volume on the disk
    private const uint MountOptionWhole = 0x00000001;     // kDADiskMountOptionWhole: every volume on the disk
    private const uint StringEncodingUtf8 = 0x08000100;    // kCFStringEncodingUTF8
    private const int RunLoopRunTimedOut = 3;              // kCFRunLoopRunTimedOut

    [DllImport(CoreFoundation)] private static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CoreFoundation)] private static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string s, uint encoding);
    [DllImport(CoreFoundation)] private static extern IntPtr CFStringGetCStringPtr(IntPtr s, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
    [DllImport(DiskArbitration)] private static extern IntPtr DASessionCreate(IntPtr allocator);
    [DllImport(DiskArbitration)] private static extern void DASessionScheduleWithRunLoop(IntPtr session, IntPtr runLoop, IntPtr mode);
    [DllImport(DiskArbitration)] private static extern void DASessionUnscheduleFromRunLoop(IntPtr session, IntPtr runLoop, IntPtr mode);
    [DllImport(DiskArbitration)] private static extern IntPtr DADiskCreateFromBSDName(IntPtr allocator, IntPtr session, [MarshalAs(UnmanagedType.LPUTF8Str)] string bsdName);
    [DllImport(DiskArbitration)] private static extern void DADiskUnmount(IntPtr disk, uint options, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> callback, IntPtr context);
    [DllImport(DiskArbitration)] private static extern void DADiskMount(IntPtr disk, IntPtr path, uint options, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> callback, IntPtr context);
    [DllImport(DiskArbitration)] private static extern int DADissenterGetStatus(IntPtr dissenter);
    [DllImport(DiskArbitration)] private static extern IntPtr DADissenterGetStatusString(IntPtr dissenter);

    private static readonly object Gate = new();
    private static volatile bool _done;
    private static string? _dissent;
    private static int _status;
    private const int NotMounted = unchecked((int)0xF8DA0007);   // kDAReturnNotMounted

    /// "disk2" out of "/dev/disk2", "/dev/rdisk2" or "disk2s1".
    public static string? BsdName(string device)
    {
        var at = device.LastIndexOf("disk", StringComparison.Ordinal);
        if (at < 0) return null;
        var name = device[at..];
        var end = name.IndexOf('s', 4);
        return end > 0 ? name[..end] : name;
    }

    /// Unmounts every volume of the disk the device is on and waits for the
    /// result, up to thirty seconds. True when the disk is unmounted (or was
    /// not mounted); false with the system's reason otherwise. On any other
    /// platform: true, nothing to do.
    public static bool Unmount(string device, out string? reason) => Arbitrate(device, mount: false, TimeSpan.FromSeconds(30), out reason);

    /// The disc handed back to macOS: its volumes mounted again, as they were
    /// before Deadwax unmounted them. An audio CD left unmounted stays in the
    /// drive after Deadwax quits, because the drive's own button only asks
    /// macOS to eject, and macOS holds nothing to eject (his test, 2026-10-20).
    public static bool Mount(string device, out string? reason) => Arbitrate(device, mount: true, TimeSpan.FromSeconds(10), out reason);

    private static bool Arbitrate(string device, bool mount, TimeSpan limit, out string? reason)
    {
        reason = null;
        if (!OperatingSystem.IsMacOS()) return true;
        var bsd = BsdName(device);
        if (bsd is null) { reason = $"{device} is not a disk device."; return false; }

        lock (Gate)
        {
            var mode = CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopDefaultMode", StringEncodingUtf8);
            var session = DASessionCreate(IntPtr.Zero);
            if (session == IntPtr.Zero) { CFRelease(mode); reason = "DiskArbitration gave no session."; return false; }
            var runLoop = CFRunLoopGetCurrent();
            var disk = IntPtr.Zero;
            try
            {
                DASessionScheduleWithRunLoop(session, runLoop, mode);
                disk = DADiskCreateFromBSDName(IntPtr.Zero, session, bsd);
                if (disk == IntPtr.Zero) { reason = $"DiskArbitration knows no {bsd}."; return false; }
                _done = false; _dissent = null; _status = 0;
                if (mount) DADiskMount(disk, IntPtr.Zero, MountOptionWhole, &Unmounted, IntPtr.Zero);
                else DADiskUnmount(disk, UnmountOptionWhole, &Unmounted, IntPtr.Zero);
                var clock = Stopwatch.StartNew();
                while (!_done && clock.Elapsed < limit)
                    CFRunLoopRunInMode(mode, 0.5, true);
                if (!_done) { reason = $"the {(mount ? "mount" : "unmount")} did not finish in {limit.TotalSeconds:0} seconds."; return false; }
                reason = _dissent;
                // "not mounted" is the outcome wanted; DiskArbitration reports it as a dissent,
                // kDAReturnNotMounted with no text (the first Mac, 2026-10-15).
                return _dissent is null || (!mount && _status == NotMounted);
            }
            finally
            {
                if (disk != IntPtr.Zero) CFRelease(disk);
                DASessionUnscheduleFromRunLoop(session, runLoop, mode);
                CFRelease(session);
                CFRelease(mode);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static void Unmounted(IntPtr disk, IntPtr dissenter, IntPtr context)
    {
        if (dissenter != IntPtr.Zero)
        {
            var status = DADissenterGetStatus(dissenter);
            var text = DADissenterGetStatusString(dissenter);
            var s = text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(CFStringGetCStringPtr(text, StringEncodingUtf8));
            _status = status;
            _dissent = s ?? $"DiskArbitration status 0x{status:X8}";
        }
        _done = true;
    }
}
