namespace Deadwax.Drive;

public sealed record DriveIdentity(string Vendor, string Model, string Revision)
{
    public override string ToString() => $"{Vendor} {Model} {Revision}";

    /// What the drive told the kernel when it was attached (its INQUIRY
    /// answer, kept in /sys/block/srN/device): the same vendor, model and
    /// revision an INQUIRY returns, but known with no disc in and without
    /// sending the drive a single command. Null if the device is not an sr drive.
    public static DriveIdentity? FromSysfs(string device)
    {
        try
        {
            var name = Path.GetFileName(new FileInfo(device).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? device);
            var dir = Path.Combine("/sys/block", name, "device");
            string Read(string file) => File.ReadAllText(Path.Combine(dir, file)).Trim();
            var identity = new DriveIdentity(Read("vendor"), Read("model"), Read("rev"));
            return identity.Vendor.Length + identity.Model.Length == 0 ? null : identity;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

public sealed class DriveException(string message) : Exception(message);

/// A CD drive with a disc in it, through Deadwax's own drive access (spec
/// §11, step 3): DKIOCCDREAD and DiskArbitration on the Mac, SG_IO on Linux,
/// SPTI on Windows. libcdio did this until 0.7.4 (the last build carrying it
/// was 0.7.3 on Windows). Dispose to release the device.
public sealed class CdDrive : IDisposable
{
    /// The drive to use when none is named.
    ///
    /// Linux: /dev/cdrom if udev made that link, otherwise the first /dev/srN.
    /// Not a fixed /dev/sr0: after a USB reset with the old device still held
    /// open, the BDR-209D came back as sr1 and no /dev/cdrom link existed
    /// (2026-09-24). Windows: the first drive letter Windows calls a CD drive.
    /// macOS: the first disk DiskArbitration says holds a CD.
    public static string DefaultDevice =>
        FoundDevice ?? (OperatingSystem.IsLinux() ? "/dev/sr0" : OperatingSystem.IsWindows() ? "D:" : "/dev/disk2");

    /// The drive the system reports, or null when it reports none and
    /// DefaultDevice is only a guess. On a Mac only a disk holding a CD is
    /// listed (a mounted Deadwax.dmg was once taken for the drive), so an
    /// empty or open drive is null there too.
    public static string? FoundDevice
    {
        get
        {
            if (OperatingSystem.IsLinux())
            {
                if (File.Exists("/dev/cdrom")) return "/dev/cdrom";
                return Directory.Exists("/dev")
                    ? Directory.EnumerateFiles("/dev", "sr*")
                        .Where(p => int.TryParse(Path.GetFileName(p)[2..], out _))
                        .OrderBy(p => int.Parse(Path.GetFileName(p)[2..]))
                        .FirstOrDefault()
                    : null;
            }
            if (OperatingSystem.IsMacOS()) return MacDisc.CdDevices().FirstOrDefault();
            if (OperatingSystem.IsWindows()) return WindowsSptiTransport.Drives().FirstOrDefault();
            return null;
        }
    }

    /// Whether a disc is in the drive and how many audio tracks it has, from
    /// the drive itself: open, read the TOC, count. (false, 0) for an empty or
    /// absent drive. Used by the window's three-second watch, which until
    /// 0.2.16 asked udev instead and so only worked on Linux.
    public static (bool Present, int Audio) Media(string? device = null)
    {
        try
        {
            using var drive = Open(device);
            var toc = drive.ReadToc();
            return (true, toc.Tracks.Count(t => t.IsAudio));
        }
        catch (DriveException) { return (false, 0); }
    }

    /// Opens the tray. False if the platform or drive would not; the device
    /// must not be held open by this process (dispose any CdDrive first).
    public static bool Eject(string? device = null)
    {
        // On a Mac an empty drive has no disk to send a command to, so the tray
        // is opened through macOS's own drutil (2026-10-20: "Open tray" did
        // nothing with the drive empty; DefaultDevice was only a guess).
        if (OperatingSystem.IsMacOS()) return MacTray();
        try { using var own = (PassThroughTransport)IDriveTransport.Open(device ?? DefaultDevice); return own.Eject(); }
        catch (DriveException) { return false; }
    }

    private static bool MacTray()
    {
        foreach (var args in new[] { new[] { "tray", "open" }, new[] { "tray", "eject" } })
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/drutil") { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) start.ArgumentList.Add(a);
                using var p = System.Diagnostics.Process.Start(start);
                if (p is null) continue;
                if (!p.WaitForExit(10000)) { p.Kill(); continue; }
                if (p.ExitCode == 0) return true;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
        return false;
    }

    private IDriveTransport? _transport;
    private IReadOnlyDictionary<int, byte>? _control;

    public string Device { get; }

    private CdDrive(string device, IDriveTransport transport)
    {
        Device = device;
        _transport = transport;
    }

    public static CdDrive Open(string? device = null)
    {
        device ??= DefaultDevice;
        // macOS mounts the disc on insertion; rippers unmount it so the
        // Finder is not reading the same disc. Best effort: the reads
        // themselves do not need it (MacDisc). Elsewhere this does nothing.
        // ONLY A CD: with the drive empty, device is DefaultDevice's guess,
        // which on another Mac may be an external disk or a disk image, and
        // this runs every three seconds.
        if (OperatingSystem.IsMacOS())
        {
            if (!MacDisc.IsCd(device))
                throw new DriveException($"No CD in {device}.");
            if (!MacDisc.Unmount(device, out var why))
                Console.Error.WriteLine($"[drive] could not unmount {device}: {why}");
        }
        return new CdDrive(device, IDriveTransport.Open(device));
    }

    public Toc ReadToc()
    {
        var answer = Transport.ReadFullToc() ?? throw new DriveException($"No disc in {Device}, or the disc has no table of contents.");
        var (toc, control) = FullToc.Parse(answer);
        _control = control;
        return toc;
    }

    public DriveIdentity? ReadIdentity() => OwnIdentity(Device, Transport);

    /// Caps the drive's read speed at `speed` times 1x. For a disc that is out
    /// of balance: Now and Zen (2026-10-05) vibrated at full speed and the
    /// BDR-209D went silent mid-command on cdrdao's subchannel pass, twice;
    /// at 4x it scanned and ripped 13/13 accurate. The drive holds the cap
    /// until the disc is changed, so it is set once before the scan and once
    /// more before the rip, in case the disc was reinserted between.
    /// Null lifts the cap (the drive chooses again).
    public void LimitSpeed(int? speed)
    {
        if (speed is < 1) throw new ArgumentOutOfRangeException(nameof(speed));
        if (!Transport.SetSpeed(speed)) throw new DriveException($"{Device} did not accept a {speed}x speed limit.");
    }

    /// Opens the device just long enough to set or lift the cap.
    public static void LimitSpeed(string? device, int? speed)
    {
        using var drive = Open(device);
        drive.LimitSpeed(speed);
    }

    /// The drive's identity by Deadwax's own means: DiskArbitration on the Mac,
    /// sysfs on Linux (no command sent), INQUIRY through the transport on Windows.
    public static DriveIdentity? OwnIdentity(string device, IDriveTransport transport) =>
        OperatingSystem.IsMacOS() ? MacDisc.Identity(device)
        : OperatingSystem.IsLinux() ? DriveIdentity.FromSysfs(device)
        : (transport as PassThroughTransport)?.Inquiry();

    /// The drive's transport, for the subchannel reader.
    internal IDriveTransport Transport => _transport ?? throw new ObjectDisposedException(nameof(CdDrive));

    /// The TOC's control bits for a track, which cdrdao's TOC file records
    /// (its Q-subchannel reading is only checked against them).
    public (bool PreEmphasis, bool CopyPermitted, bool FourChannel) TrackFlags(int track)
    {
        if (_control is null) ReadToc();
        var c = _control!.GetValueOrDefault(track);
        return ((c & 0x01) != 0, (c & 0x02) != 0, (c & 0x08) != 0);
    }

    public void Dispose()
    {
        _transport?.Dispose();
        _transport = null;
    }
}
