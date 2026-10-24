namespace Deadwax.Drive;

public sealed record DriveIdentity(string Vendor, string Model, string Revision)
{
    public override string ToString() => $"{Vendor} {Model} {Revision}";

    /// What the drive told the kernel when it was attached (its INQUIRY
    /// answer, kept in /sys/block/srN/device): the same vendor, model and
    /// revision libcdio reads, but known with no disc in and without sending
    /// the drive a single command. Null if the device is not an sr drive.
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

/// A CD drive with a disc in it. Holds libcdio's handle; dispose to release it.
public sealed class CdDrive : IDisposable
{
    /// The drive to use when none is named.
    ///
    /// Linux: /dev/cdrom if udev made that link, otherwise the first /dev/srN.
    /// Not a fixed /dev/sr0: after a USB reset with the old device still held
    /// open, the BDR-209D came back as sr1 and no /dev/cdrom link existed
    /// (2026-09-24). Elsewhere libcdio's own list of drives (a drive letter on
    /// Windows, /dev/diskN on macOS), or its default when the list is empty.
    public static string DefaultDevice =>
        FoundDevice ?? (OperatingSystem.IsLinux() ? "/dev/sr0" : OperatingSystem.IsWindows() ? "D:" : "/dev/disk2");

    /// The drive the system reports, or null when it reports none and
    /// DefaultDevice is only a guess. On a Mac, libcdio lists a drive only
    /// while a disc is in it, so an empty or open drive is null here too.
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
            try
            {
                // On a Mac only a disk macOS says holds a CD: libcdio lists disk
                // images too, and a mounted Deadwax.dmg came first (MacDisc.IsCd).
                if (OperatingSystem.IsMacOS())
                    return OwnDriveAccess ? MacDisc.CdDevices().FirstOrDefault()
                        : LibCdio.Devices().FirstOrDefault(MacDisc.IsCd)
                          ?? (LibCdio.DefaultDevice() is { } d && MacDisc.IsCd(d) ? d : null);
                return LibCdio.Devices().FirstOrDefault() ?? LibCdio.DefaultDevice();
            }
            catch (DllNotFoundException)
            {
                return null;
            }
        }
    }

    /// Whether a disc is in the drive and how many audio tracks it has, from
    /// the drive itself: open, read the TOC, count. (false, 0) for an empty or
    /// absent drive, or no libcdio. Used by the window's three-second watch,
    /// which until 0.2.16 asked udev instead and so only worked on Linux.
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
        // On a Mac libcdio sees a drive only while a disc is in it, so an empty
        // drive is opened through macOS's own drutil (2026-10-20: "Open tray"
        // did nothing with the drive empty; DefaultDevice was only a guess).
        // The window passes DefaultDevice, which is that same guess when nothing is found.
        if (OperatingSystem.IsMacOS() && (OwnDriveAccess || FoundDevice is null && (device is null || device == DefaultDevice))) return MacTray();
        try { return LibCdio.cdio_eject_media_drive(device ?? DefaultDevice) == 0; }
        catch (DllNotFoundException) { return false; }
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

    /// Deadwax's own drive access instead of libcdio's (step 3, spec §11): the
    /// only one on the Mac since 0.7.2, where the build carries no libcdio
    /// (proven the same by check-drive on Control, 2026-10-24; his call to take
    /// libcdio and paranoia out at once rather than a release later). Linux
    /// and Windows keep libcdio until their own READ CD transport exists.
    public static bool OwnDriveAccess => OperatingSystem.IsMacOS();

    private IntPtr _cdio;
    private MacIoctlTransport? _mac;
    private IReadOnlyDictionary<int, byte>? _control;

    public string Device { get; }

    private CdDrive(string device, MacIoctlTransport mac)
    {
        Device = device;
        _mac = mac;
    }

    private CdDrive(string device, IntPtr cdio)
    {
        Device = device;
        _cdio = cdio;
    }

    /// libcdio forces libcdio's drive access whatever OwnDriveAccess says, for check-drive.
    public static CdDrive Open(string? device = null, bool libcdio = false)
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
        if (OwnDriveAccess && !libcdio) return new CdDrive(device, new MacIoctlTransport(device));
        IntPtr cdio;
        try
        {
            cdio = LibCdio.cdio_open_cd(device);
        }
        catch (DllNotFoundException)
        {
            throw new DriveException($"libcdio is not installed (looked for {NativeLibraries.Expected("cdio")}).");
        }
        if (cdio == IntPtr.Zero)
            throw new DriveException($"Could not open {device}. Is there a disc in the drive?");
        return new CdDrive(device, cdio);
    }

    public Toc ReadToc()
    {
        if (_mac is not null)
        {
            var answer = _mac.ReadFullToc() ?? throw new DriveException($"No disc in {Device}, or the disc has no table of contents.");
            var (toc, control) = FullToc.Parse(answer);
            _control = control;
            return toc;
        }
        var handle = Handle;
        var first = LibCdio.cdio_get_first_track_num(handle);
        var count = LibCdio.cdio_get_num_tracks(handle);
        if (first == LibCdio.InvalidTrack || count == LibCdio.InvalidTrack || count == 0)
            throw new DriveException($"No disc in {Device}, or the disc has no table of contents.");

        var tracks = new List<TocTrack>(count);
        for (var n = first; n < first + count; n++)
        {
            var lsn = LibCdio.cdio_get_track_lsn(handle, n);
            if (lsn == LibCdio.InvalidLsn) throw new DriveException($"The drive gave no start for track {n}.");
            tracks.Add(new TocTrack(n, lsn, LibCdio.cdio_get_track_format(handle, n) == LibCdio.TrackFormatAudio));
        }

        var leadout = LibCdio.cdio_get_track_lsn(handle, LibCdio.LeadoutTrack);
        if (leadout == LibCdio.InvalidLsn) throw new DriveException("The drive gave no lead-out position.");
        return new Toc(tracks, leadout);
    }

    public unsafe DriveIdentity? ReadIdentity()
    {
        if (_mac is not null) return MacDisc.Identity(Device);
        var buffer = stackalloc byte[LibCdio.HwInfoSize];
        if (!LibCdio.cdio_get_hwinfo(Handle, buffer)) return null;

        var span = new ReadOnlySpan<byte>(buffer, LibCdio.HwInfoSize);
        var vendor = Field(span, 0, LibCdio.HwVendorLength);
        var model = Field(span, LibCdio.HwVendorLength + 1, LibCdio.HwModelLength);
        var revision = Field(span, LibCdio.HwVendorLength + 1 + LibCdio.HwModelLength + 1, LibCdio.HwRevisionLength);
        return new DriveIdentity(vendor, model, revision);

        static string Field(ReadOnlySpan<byte> all, int start, int length)
        {
            var field = all.Slice(start, length);
            var nul = field.IndexOf((byte)0);
            if (nul >= 0) field = field[..nul];
            return System.Text.Encoding.ASCII.GetString(field).Trim();
        }
    }

    /// Caps the drive's read speed at `speed` times 1x. For a disc that is out
    /// of balance: Now and Zen (2026-10-05) vibrated at full speed and the
    /// BDR-209D went silent mid-command on cdrdao's subchannel pass, twice;
    /// at 4x it scanned and ripped 13/13 accurate. The drive holds the cap
    /// until the disc is changed, so it is set once before the scan and once
    /// more before the rip, in case the disc was reinserted between.
    /// Null lifts the cap (CDROM_SELECT_SPEED 0: the drive chooses again).
    public void LimitSpeed(int? speed)
    {
        if (speed is < 1) throw new ArgumentOutOfRangeException(nameof(speed));
        if (OperatingSystem.IsMacOS())
        {
            // libcdio's set_speed on macOS passes the value where the ioctl
            // wants a pointer (security scan 2026-10-12, L2); do it directly.
            if (_mac is not null)
            {
                if (!_mac.SetSpeed(speed)) throw new DriveException($"{Device} did not accept a {speed}x speed limit.");
                return;
            }
            using var mac = new MacIoctlTransport(Device);
            if (!mac.SetSpeed(speed)) throw new DriveException($"{Device} did not accept a {speed}x speed limit.");
            return;
        }
        if (LibCdio.cdio_set_speed(Handle, speed ?? 0) != 0)
            throw new DriveException($"{Device} did not accept a {speed}x speed limit.");
    }

    /// Opens the device just long enough to set or lift the cap.
    public static void LimitSpeed(string? device, int? speed)
    {
        using var drive = Open(device);
        drive.LimitSpeed(speed);
    }

    private IntPtr Handle => _cdio != IntPtr.Zero ? _cdio : throw new ObjectDisposedException(nameof(CdDrive));

    /// libcdio's handle, for the subchannel reader's MMC commands.
    internal IntPtr NativeHandle => Handle;

    /// The TOC's control bits for a track, which cdrdao's TOC file records
    /// (its Q-subchannel reading is only checked against them).
    public (bool PreEmphasis, bool CopyPermitted, bool FourChannel) TrackFlags(int track)
    {
        if (_mac is not null)
        {
            if (_control is null) ReadToc();
            var c = _control!.GetValueOrDefault(track);
            return ((c & 0x01) != 0, (c & 0x02) != 0, (c & 0x08) != 0);
        }
        var n = (byte)track;
        return (LibCdio.cdio_get_track_preemphasis(Handle, n) == LibCdio.TrackFlagTrue,
                LibCdio.cdio_get_track_copy_permit(Handle, n) == LibCdio.TrackFlagTrue,
                LibCdio.cdio_get_track_channels(Handle, n) == 4);
    }

    /// libcdio's own list of drives, for check-drive to compare with ours.
    public static IReadOnlyList<string> LibCdioDevices() => LibCdio.Devices();

    /// "libcdio-paranoia (libcdio 2.3.0)", for the rip log.
    public static string EngineDescription => OperatingSystem.IsMacOS()
        ? SecureReader.EngineDescription
        : $"libcdio-paranoia (libcdio {LibCdio.Version() ?? "unknown"})";

    public void Dispose()
    {
        _mac?.Dispose();
        _mac = null;
        if (_cdio == IntPtr.Zero) return;
        LibCdio.cdio_destroy(_cdio);
        _cdio = IntPtr.Zero;
    }
}
