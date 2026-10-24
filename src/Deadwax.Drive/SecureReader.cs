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

    /// Called from the read, on the reading thread, the moment a sector turns
    /// Recovered or Skipped (the absolute sector, its new state): the read map
    /// shows trouble as it happens instead of when the pass ends.
    internal Action<int, SectorState>? Damaged { get; init; }
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
        if ((uint)i < (uint)Sectors.Length && state > Sectors[i])
        {
            Sectors[i] = state;
            if (state >= SectorState.Recovered) Damaged?.Invoke(FirstSector + i, state);
        }
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

    /// The sectors that hold bytes [fromByte, toByte) of the track, and where
    /// those bytes start inside them: a re-read of part of a track (the edges
    /// AccurateRip leaves out, spec §11 step 0) cut the same way as a whole one.
    public ReadWindow Part(int fromByte, int toByte)
    {
        if (fromByte < 0 || toByte > TrackBytes || fromByte >= toByte) throw new ArgumentOutOfRangeException(nameof(fromByte));
        var first = FirstSector + (TrackStartInWindow + fromByte) / SectorBytes;
        var end = FirstSector + (TrackStartInWindow + toByte + SectorBytes - 1) / SectorBytes;
        var readableStart = Math.Clamp(first, ReadableStart, ReadableEnd);
        var readableEnd = Math.Clamp(end, readableStart, ReadableEnd);
        return new ReadWindow(first, end, readableStart, readableEnd,
            TrackStartInWindow + fromByte - (first - FirstSector) * SectorBytes, toByte - fromByte);
    }
}

/// Reads tracks with the drive's read offset corrected (see ReadWindow): on
/// the Mac through Deadwax's own PassReader (step 4, spec §11; the only reader
/// there since 0.7.2, after Dido's Life for Rent and Led Zeppelin I read
/// better with it than with paranoia), elsewhere through libcdio-paranoia
/// until those systems have their own READ CD transport.
public sealed class SecureReader : IDisposable
{
    public const int DefaultMaxRetries = 20;

    /// Whether this run reads audio with Deadwax's own reader.
    public static bool OwnReader => OperatingSystem.IsMacOS()
        || OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DEADWAX_READER") == "deadwax";   // Linux: SG_IO, being proven

    /// The rip log's extraction engine.
    public static string EngineDescription => OwnReader
        ? $"Deadwax {typeof(SecureReader).Assembly.GetName().Version?.ToString(3)} secure read ({(OperatingSystem.IsMacOS() ? "DKIOCCDREAD" : "SG_IO")}, C2)"
        : CdDrive.EngineDescription;

    private IntPtr _drive;
    private IDriveTransport? _transport;
    private Source? _source;
    private PassReader? _own;

    public string Device { get; }

    private SecureReader(string device, IntPtr drive)
    {
        Device = device;
        _drive = drive;
    }

    private SecureReader(string device, IDriveTransport mac)
    {
        Device = device;
        _transport = mac;
        _source = new Source(mac);
        _own = new PassReader(_source);
    }

    /// The Mac transport as PassReader's audio source; AudioEnd is what the
    /// reads so far have shown of the disc (a TOC's audio lead-out when one is known).
    private sealed class Source(IDriveTransport mac) : IAudioSource
    {
        public int AudioEnd { get; set; }
        public byte[]? ReadAudio(int lsn, int count, bool c2) => mac.ReadAudio(lsn, count, c2);
    }

    public static SecureReader Open(string? device = null)
    {
        device ??= CdDrive.DefaultDevice;
        if (OwnReader) return new SecureReader(device, IDriveTransport.Open(device));
        IntPtr drive;
        try
        {
            drive = LibParanoia.cdio_cddap_identify(device, LibParanoia.MessageForgetIt, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            throw new DriveException($"libcdio-paranoia is not installed (looked for {NativeLibraries.Expected("cdio_cdda")} and {NativeLibraries.Expected("cdio_paranoia")}).");
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
        int maxRetries = DefaultMaxRetries, Action<int, int>? progress = null, CancellationToken ct = default,
        Action<int, SectorState>? damaged = null, Action<RetryStatus>? retrying = null)
    {
        var track = toc.Track(trackNumber);
        if (!track.IsAudio) throw new ArgumentException($"Track {trackNumber} is data, not audio.");

        var w = ReadWindow.For(toc, track, offsetSamples);
        if (_source is not null) _source.AudioEnd = Math.Max(_source.AudioEnd, toc.AudioLeadoutLsn);
        var (window, report) = ReadSectors(w.FirstSector, w.EndSector, w.ReadableStart, w.ReadableEnd, maxRetries, progress, ct, damaged, retrying);
        return (window.AsSpan(w.TrackStartInWindow, w.TrackBytes).ToArray(), report);
    }

    /// Bytes [fromByte, toByte) of one track, read again on their own (a new
    /// paranoia instance, as every pass gets).
    public (byte[] Audio, ReadReport Report) ReadTrackPart(
        Toc toc, int trackNumber, int offsetSamples, int fromByte, int toByte,
        int maxRetries = DefaultMaxRetries, CancellationToken ct = default)
    {
        var track = toc.Track(trackNumber);
        if (!track.IsAudio) throw new ArgumentException($"Track {trackNumber} is data, not audio.");

        var p = ReadWindow.For(toc, track, offsetSamples).Part(fromByte, toByte);
        if (_source is not null) _source.AudioEnd = Math.Max(_source.AudioEnd, toc.AudioLeadoutLsn);
        var (window, report) = ReadSectors(p.FirstSector, p.EndSector, p.ReadableStart, p.ReadableEnd, maxRetries, ct: ct);
        return (window.AsSpan(p.TrackStartInWindow, p.TrackBytes).ToArray(), report);
    }

    /// One pass over a run of sectors, through paranoia: [firstSector, endSector)
    /// comes back as bytes, of which only [readableStart, readableEnd) is read
    /// from the disc; the rest stays zero (before the disc, or past the audio).
    /// ReadTrack cuts a track out of this; OffsetFinder reads a track with a
    /// margin on both sides and slides over it.
    public (byte[] Window, ReadReport Report) ReadSectors(
        int firstSector, int endSector, int readableStart, int readableEnd,
        int maxRetries = DefaultMaxRetries, Action<int, int>? progress = null, CancellationToken ct = default,
        Action<int, SectorState>? damaged = null, Action<RetryStatus>? retrying = null)
    {
        if (_own is not null)
        {
            _source!.AudioEnd = Math.Max(_source.AudioEnd, readableEnd);
            return _own.ReadSectors(firstSector, endSector, readableStart, readableEnd, maxRetries, progress, ct, damaged, retrying);
        }
        var window = new byte[(long)(endSector - firstSector) * LibParanoia.SectorBytes];
        var report = new ReadReport(firstSector, endSector - firstSector) { Damaged = damaged };
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
        return (window, report);
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
        _transport?.Dispose();
        _transport = null;
        if (_drive == IntPtr.Zero) return;
        LibParanoia.cdio_cddap_close(_drive);
        _drive = IntPtr.Zero;
    }
}
