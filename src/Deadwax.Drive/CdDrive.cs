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
    /// The drive to use when none is named: /dev/cdrom if udev made that link,
    /// otherwise the first /dev/srN. Not a fixed /dev/sr0: after a USB reset
    /// with the old device still held open, the BDR-209D came back as sr1 and
    /// no /dev/cdrom link existed (2026-09-24).
    public static string DefaultDevice
    {
        get
        {
            if (File.Exists("/dev/cdrom")) return "/dev/cdrom";
            var sr = Directory.Exists("/dev")
                ? Directory.EnumerateFiles("/dev", "sr*")
                    .Where(p => int.TryParse(Path.GetFileName(p)[2..], out _))
                    .OrderBy(p => int.Parse(Path.GetFileName(p)[2..]))
                    .FirstOrDefault()
                : null;
            return sr ?? "/dev/sr0";
        }
    }

    private IntPtr _cdio;

    public string Device { get; }

    private CdDrive(string device, IntPtr cdio)
    {
        Device = device;
        _cdio = cdio;
    }

    public static CdDrive Open(string? device = null)
    {
        device ??= DefaultDevice;
        IntPtr cdio;
        try
        {
            cdio = LibCdio.cdio_open_cd(device);
        }
        catch (DllNotFoundException)
        {
            throw new DriveException("libcdio is not installed (looked for libcdio.so.19).");
        }
        if (cdio == IntPtr.Zero)
            throw new DriveException($"Could not open {device}. Is there a disc in the drive?");
        return new CdDrive(device, cdio);
    }

    public Toc ReadToc()
    {
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

    /// "libcdio-paranoia (libcdio 2.3.0)", for the rip log.
    public static string EngineDescription => $"libcdio-paranoia (libcdio {LibCdio.Version() ?? "unknown"})";

    public void Dispose()
    {
        if (_cdio == IntPtr.Zero) return;
        LibCdio.cdio_destroy(_cdio);
        _cdio = IntPtr.Zero;
    }
}
