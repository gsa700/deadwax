using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Deadwax.Drive;

/// How one sector read, worst event wins. The order is the read map's key.
public enum SectorState : byte
{
    NotRead = 0,
    Clean,
    Recovered,   // paranoia had to re-read or repair it, and did
    Skipped,     // paranoia gave up and filled it in: the audio there is a guess
}

/// Counts of what paranoia reported while reading, and the state of every
/// sector of the track. A clean disc on this drive shows only reads, verifies
/// and edge fix-ups (normal jitter correction).
public sealed class ReadReport
{
    private readonly int[] _events = new int[16];

    public ReadReport(int firstSector, int sectors)
    {
        FirstSector = firstSector;
        Sectors = new SectorState[sectors];
    }

    public int FirstSector { get; }
    public SectorState[] Sectors { get; }
    public TimeSpan Elapsed { get; internal set; }

    public int this[ParanoiaEvent e] => _events[(int)e];

    public int Skips => this[ParanoiaEvent.Skip];
    public int ReadErrors => this[ParanoiaEvent.ReadError];
    public int Repairs => this[ParanoiaEvent.Scratch] + this[ParanoiaEvent.Repair]
                          + this[ParanoiaEvent.FixupDropped] + this[ParanoiaEvent.FixupDuped];

    internal void Record(int e, long wordPosition)
    {
        if ((uint)e < _events.Length) _events[e]++;
        var state = (ParanoiaEvent)e switch
        {
            ParanoiaEvent.Skip => SectorState.Skipped,
            ParanoiaEvent.ReadError or ParanoiaEvent.Scratch or ParanoiaEvent.Repair
                or ParanoiaEvent.FixupDropped or ParanoiaEvent.FixupDuped => SectorState.Recovered,
            ParanoiaEvent.Read or ParanoiaEvent.Verify or ParanoiaEvent.Wrote => SectorState.Clean,
            _ => SectorState.NotRead,
        };
        if (state == SectorState.NotRead) return;
        var i = (int)(wordPosition / LibParanoia.SectorWords) - FirstSector;
        if ((uint)i < (uint)Sectors.Length && state > Sectors[i]) Sectors[i] = state;
    }
}

/// What paranoia's callback reports (paranoia.h, paranoia_cb_mode_t).
public enum ParanoiaEvent
{
    Read = 0, Verify, FixupEdge, FixupAtom, Scratch, Repair, Skip, Drift,
    Backoff, Overlap, FixupDropped, FixupDuped, ReadError, CacheError, Wrote, Finished,
}

/// Which sectors to read for one track, and where the track sits inside them.
///
/// A drive with read offset +667 hands back audio 667 samples early, so the true
/// audio of a track starting at sample S is read from S + 667. That window never
/// lines up with sectors, so a pass reads one sector more than the track and
/// cuts the track out of it. Sectors before the disc or past the end of the audio
/// cannot be read and stay zero: the last 667 samples of the last track, as in
/// every whipper rip made without over-reading.
public readonly record struct ReadWindow(
    int FirstSector, int EndSector, int ReadableStart, int ReadableEnd, int TrackStartInWindow, int TrackBytes)
{
    public const int SectorBytes = 2352;

    public static ReadWindow For(Toc toc, TocTrack track, int offsetSamples)
    {
        var offsetBytes = (long)offsetSamples * 4;
        var firstByte = (long)track.StartLsn * SectorBytes + offsetBytes;
        var endByte = (long)toc.EndLsn(track) * SectorBytes + offsetBytes;
        var firstSector = (int)Math.Floor(firstByte / (double)SectorBytes);
        var endSector = (int)Math.Ceiling(endByte / (double)SectorBytes);

        // An enhanced CD's data session starts after AudioLeadoutLsn but is not
        // audio, so the audio lead-out, not the disc's, is the limit.
        var readableStart = Math.Max(firstSector, 0);
        var readableEnd = Math.Min(endSector, toc.AudioLeadoutLsn);

        return new ReadWindow(firstSector, endSector, readableStart, Math.Max(readableEnd, readableStart),
            (int)(firstByte - (long)firstSector * SectorBytes), (int)(endByte - firstByte));
    }
}

/// Reads tracks through paranoia, with the drive's read offset corrected
/// (see ReadWindow).
public sealed class SecureReader : IDisposable
{
    public const int DefaultMaxRetries = 20;

    private IntPtr _drive;

    public string Device { get; }

    private SecureReader(string device, IntPtr drive)
    {
        Device = device;
        _drive = drive;
    }

    public static SecureReader Open(string? device = null)
    {
        device ??= CdDrive.DefaultDevice;
        IntPtr drive;
        try
        {
            drive = LibParanoia.cdio_cddap_identify(device, LibParanoia.MessageForgetIt, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            throw new DriveException("libcdio-paranoia is not installed (looked for libcdio_cdda.so.2 and libcdio_paranoia.so.2).");
        }
        if (drive == IntPtr.Zero) throw new DriveException($"{device} is not a CD drive paranoia can use, or it is empty.");
        if (LibParanoia.cdio_cddap_open(drive) != 0)
        {
            LibParanoia.cdio_cddap_close(drive);
            throw new DriveException($"Could not open the disc in {device} for reading.");
        }
        return new SecureReader(device, drive);
    }

    /// One pass over one track. Each pass gets its own paranoia instance, so a
    /// second pass is a real second read, not paranoia's cache of the first.
    public (byte[] Audio, ReadReport Report) ReadTrack(
        Toc toc, int trackNumber, int offsetSamples,
        int maxRetries = DefaultMaxRetries, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var track = toc.Track(trackNumber);
        if (!track.IsAudio) throw new ArgumentException($"Track {trackNumber} is data, not audio.");

        var w = ReadWindow.For(toc, track, offsetSamples);
        var (firstSector, endSector, readableStart, readableEnd) = (w.FirstSector, w.EndSector, w.ReadableStart, w.ReadableEnd);

        var window = new byte[(long)(endSector - firstSector) * LibParanoia.SectorBytes];
        var report = new ReadReport(firstSector, endSector - firstSector);
        var clock = Stopwatch.StartNew();

        var paranoia = LibParanoia.cdio_paranoia_init(Handle);
        if (paranoia == IntPtr.Zero) throw new DriveException("paranoia could not start on this drive.");
        try
        {
            LibParanoia.cdio_paranoia_modeset(paranoia, LibParanoia.ModeFull & ~LibParanoia.ModeNeverSkip);
            if (readableEnd > readableStart)
            {
                LibParanoia.cdio_paranoia_seek(paranoia, readableStart, LibParanoia.SeekSet);
                Sink.Current = report;
                try
                {
                    for (var sector = readableStart; sector < readableEnd; sector++)
                    {
                        ct.ThrowIfCancellationRequested();
                        unsafe
                        {
                            var data = LibParanoia.cdio_paranoia_read_limited(paranoia, &Sink.Callback, maxRetries);
                            if (data == null) throw new DriveException($"Read failed at sector {sector}.");
                            new ReadOnlySpan<byte>(data, LibParanoia.SectorBytes)
                                .CopyTo(window.AsSpan((int)((long)(sector - firstSector) * LibParanoia.SectorBytes)));
                        }
                        progress?.Invoke(sector - readableStart + 1, readableEnd - readableStart);
                    }
                }
                finally
                {
                    Sink.Current = null;
                }
            }
        }
        finally
        {
            LibParanoia.cdio_paranoia_free(paranoia);
        }
        report.Elapsed = clock.Elapsed;

        return (window.AsSpan(w.TrackStartInWindow, w.TrackBytes).ToArray(), report);
    }

    /// paranoia calls back from inside a read on the calling thread, with no
    /// user argument to say which read, so the report being filled is per thread.
    private static class Sink
    {
        [ThreadStatic] public static ReadReport? Current;

        [UnmanagedCallersOnly]
        public static void Callback(CLong position, int e) => Current?.Record(e, (long)position.Value);
    }

    private IntPtr Handle => _drive != IntPtr.Zero ? _drive : throw new ObjectDisposedException(nameof(SecureReader));

    public void Dispose()
    {
        if (_drive == IntPtr.Zero) return;
        LibParanoia.cdio_cddap_close(_drive);
        _drive = IntPtr.Zero;
    }
}
