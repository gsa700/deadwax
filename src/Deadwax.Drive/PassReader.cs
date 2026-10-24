using System.Diagnostics;

namespace Deadwax.Drive;

/// What PassReader needs from a drive: audio by the sector, with C2 error
/// flags when the drive gives them. MacIoctlTransport on the Mac (DKIOCCDREAD);
/// READ CD through SG_IO and SPTI to come.
public interface IAudioSource
{
    /// `count` sectors from `lsn`: 2352 bytes of audio each, followed with
    /// `c2` by the 294 bytes of C2 flags. Null when the read failed.
    byte[]? ReadAudio(int lsn, int count, bool c2);

    /// The disc's last readable audio sector + 1: where a cache-defeating read
    /// may go.
    int AudioEnd { get; }
}

/// A damaged sector being read again, for the window and the command line:
/// which sector, which try of how many, how many damaged sectors this pass has
/// met so far, and once it is decided, Recovered or Skipped.
public sealed record RetryStatus(int Sector, int Attempt, int Tries, int Damaged, SectorState? Result);

/// One pass over a run of sectors with Deadwax's own error handling, in place
/// of libcdio-paranoia's (step 4 of the plan to need no native libraries,
/// spec §11). What a whole track is worth is decided above this, as before:
/// AccurateRip on one read, else two passes that agree (RipSession). What a
/// pass must do is read every sector, notice the ones that came back bad,
/// and try to get them right:
///
/// - Sectors are read 24 at a time with the drive's C2 error flags, its own
///   report of bytes it could not correct and guessed at. A run that comes
///   back whole, with no C2 bit set, is clean.
/// - A run that fails, and every sector with a C2 bit set, is read again on
///   its own: each time after a read far away, so the drive's cache holds
///   something else and the sector comes from the disc again. It counts as
///   recovered once two reads with no C2 bit agree byte for byte. It is
///   skipped after MaxTries reads (his "limit it to 5", 2026-10-24: twenty,
///   paranoia's figure, took ten minutes a pass on Dido's Life for Rent,
///   track 11), or sooner when three flagged reads in a row come back the same
///   (the drive concealing the damage the same way each time: no read will do
///   better), keeping the read with the fewest C2 bits: the drive's own
///   concealment, which "Make best copy" (spec §8) builds on. Zeros only when
///   no read came back at all.
/// - A pass has a time budget, the run's own playing time plus a minute:
///   once it is spent, a damaged sector gets one read, so a bad patch cannot
///   hold the drive for an hour (the watchdog of spec §8).
/// - Without C2 (a drive that will not give it) a run is clean when it reads,
///   and only a failed read is retried: the two passes above are then what
///   finds a bad sector, as they would with paranoia's own guesses.
///
/// The read report is paranoia's shape (ReadReport, ParanoiaEvent), so the
/// read map, the log and RipSession read it unchanged.
public sealed class PassReader(IAudioSource source)
{
    public const int SectorBytes = 2352;
    public const int C2Bytes = 294;
    public const int RunSectors = 24;

    /// How far away the cache-defeating read goes: past the drive's cache
    /// (the BDR-209D has 4 MB, about 1,800 sectors) and its read-ahead, and
    /// no further: every try is a seek there and back.
    public const int CacheDefeatDistance = 3_000;

    /// The most reads of one damaged sector.
    public const int MaxTries = 5;

    private bool? _c2;   // whether the drive gives C2 flags; found on the first read

    /// The same contract as SecureReader.ReadSectors: [firstSector, endSector)
    /// comes back as bytes, of which only [readableStart, readableEnd) is read.
    public (byte[] Window, ReadReport Report) ReadSectors(
        int firstSector, int endSector, int readableStart, int readableEnd, int maxRetries,
        Action<int, int>? progress = null, CancellationToken ct = default, Action<int, SectorState>? damaged = null,
        Action<RetryStatus>? retrying = null)
    {
        var window = new byte[(long)(endSector - firstSector) * SectorBytes];
        var report = new ReadReport(firstSector, endSector - firstSector) { Damaged = damaged };
        var clock = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(0, readableEnd - readableStart) / (double)Toc.SectorsPerSecond + 60);
        var tries = Math.Clamp(maxRetries, 1, MaxTries);
        var damagedSoFar = 0;

        for (var at = readableStart; at < readableEnd; at += RunSectors)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(RunSectors, readableEnd - at);
            var run = ReadRun(at, n);
            for (var i = 0; i < n; i++)
            {
                var sector = at + i;
                var target = window.AsSpan((int)((long)(sector - firstSector) * SectorBytes), SectorBytes);
                if (run is not null && !HasC2(run, i))
                {
                    Audio(run, i).CopyTo(target);
                    report.Record((int)ParanoiaEvent.Read, Word(sector));
                }
                else
                {
                    report.Record((int)ParanoiaEvent.ReadError, Word(sector));
                    damagedSoFar++;
                    var n2 = clock.Elapsed < budget ? tries : 1;
                    var (audio, recovered) = Secure(sector, n2, ct, attempt => retrying?.Invoke(new RetryStatus(sector, attempt, n2, damagedSoFar, null)));
                    audio.CopyTo(target);
                    report.Record((int)(recovered ? ParanoiaEvent.Repair : ParanoiaEvent.Skip), Word(sector));
                    retrying?.Invoke(new RetryStatus(sector, n2, n2, damagedSoFar, recovered ? SectorState.Recovered : SectorState.Skipped));
                }
            }
            progress?.Invoke(at + n - readableStart, readableEnd - readableStart);
        }
        report.Elapsed = clock.Elapsed;
        return (window, report);
    }

    private int Stride => _c2 == true ? SectorBytes + C2Bytes : SectorBytes;

    /// One run, with C2 when the drive gives it. The first read finds out: a
    /// drive that refuses a C2 read but answers a plain one has no C2.
    private byte[]? ReadRun(int lsn, int count)
    {
        if (_c2 is null)
        {
            var withC2 = source.ReadAudio(lsn, count, c2: true);
            if (withC2 is not null) { _c2 = true; Console.Error.WriteLine("[read] the drive gives C2 error flags"); return withC2; }
            var plain = source.ReadAudio(lsn, count, c2: false);
            if (plain is not null) { _c2 = false; Console.Error.WriteLine("[read] the drive gives no C2 error flags: only failed reads are retried"); }
            return plain;
        }
        return source.ReadAudio(lsn, count, _c2.Value);
    }

    /// One sector read until two clean reads agree, or `tries` reads, or three
    /// flagged reads in a row come back the same (two was too few: a sector
    /// can read badly the same way twice and then cleanly).
    private (byte[] Audio, bool Recovered) Secure(int sector, int tries, CancellationToken ct, Action<int>? attempting = null)
    {
        byte[]? lastClean = null, best = null, lastFlagged = null;
        var sameGuess = 0;
        var bestFlags = int.MaxValue;
        for (var attempt = 0; attempt < Math.Max(1, tries); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            attempting?.Invoke(attempt + 1);
            DefeatCache(sector);
            var read = ReadRun(sector, 1);
            if (read is null) continue;
            var audio = Audio(read, 0).ToArray();
            var flags = _c2 == true ? C2Count(read, 0) : 0;
            if (flags == 0)
            {
                if (lastClean is not null && lastClean.AsSpan().SequenceEqual(audio)) return (audio, true);
                // Without C2 one clean read is all there is to go on.
                if (_c2 != true) return (audio, true);
                lastClean = audio;
            }
            if (flags < bestFlags) { best = audio; bestFlags = flags; }
            if (flags > 0)
            {
                sameGuess = lastFlagged is not null && lastFlagged.AsSpan().SequenceEqual(audio) ? sameGuess + 1 : 1;
                if (sameGuess == 3) break;   // the same guess three times
                lastFlagged = audio;
            }
        }
        return (best ?? new byte[SectorBytes], false);
    }

    /// A read far from the sector, so the next read of it is the disc's, not
    /// the drive's cache.
    private void DefeatCache(int sector)
    {
        var away = sector + CacheDefeatDistance < source.AudioEnd ? sector + CacheDefeatDistance : Math.Max(0, sector - CacheDefeatDistance);
        _ = source.ReadAudio(away, 1, c2: false);
    }

    private ReadOnlySpan<byte> Audio(byte[] run, int i) => run.AsSpan(i * Stride, SectorBytes);

    private bool HasC2(byte[] run, int i) => _c2 == true && run.AsSpan(i * Stride + SectorBytes, C2Bytes).IndexOfAnyExcept((byte)0) >= 0;

    private int C2Count(byte[] run, int i)
    {
        var count = 0;
        foreach (var b in run.AsSpan(i * Stride + SectorBytes, C2Bytes)) count += System.Numerics.BitOperations.PopCount(b);
        return count;
    }

    /// The sample-word position paranoia reports a sector by (ReadReport.Record).
    private static long Word(int sector) => (long)sector * LibParanoia.SectorWords;
}
