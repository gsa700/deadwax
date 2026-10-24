using Deadwax.Drive;

namespace Deadwax.Tests;

/// PassReader, step 4's secure read (spec §11), against a drive made up for
/// the purpose: a disc of known audio, sectors that fail or come back wrong
/// with C2 flags, a cache, and a drive with no C2 at all.
public class PassReaderTests
{
    private const int S = PassReader.SectorBytes;

    private sealed class FakeDrive(int sectors) : IAudioSource
    {
        public readonly byte[] Disc = Enumerable.Range(0, sectors * S).Select(i => (byte)(i * 7 + i / S)).ToArray();
        public bool C2Supported = true;
        /// Sector -> how many more reads of it come back bad (wrong bytes, C2 set).
        public readonly Dictionary<int, int> BadReads = [];
        /// Sector -> how many more reads of it come back wrong WITHOUT a C2 flag
        /// (after its BadReads are used up): the drive silently wrong.
        public readonly Dictionary<int, int> SilentReads = [];
        /// Sectors that never read cleanly.
        public readonly HashSet<int> Dead = [];
        /// Sectors whose read fails outright while BadReads lasts, instead of coming back wrong.
        public readonly HashSet<int> Fails = [];
        /// The drive's cache: the last run it read. A re-read of it comes from here.
        private (int Lsn, byte[] Data)? _cache;
        public int Reads;
        /// Reads of each sector on its own (count 1), the cache-defeating ones included.
        public readonly Dictionary<int, int> SingleReads = [];
        /// Dead sectors come back a different wrong way each time unless this is set.
        public bool SameGuess;
        private int _guess;

        public int AudioEnd => sectors;
        public readonly List<int?> Speeds = [];
        public bool SetSpeed(int? multiple) { Speeds.Add(multiple); return true; }
        /// The BDR-209D's way (2026-10-24): a read that crosses one of these ends early.
        public readonly List<int> TrackStarts = [];

        public byte[]? ReadAudio(int lsn, int count, bool c2)
        {
            Reads++;
            if (count == 1) SingleReads[lsn] = SingleReads.GetValueOrDefault(lsn) + 1;
            if (c2 && !C2Supported) return null;
            if (lsn < 0 || lsn + count > sectors) return null;
            if (TrackStarts.Any(b => b > lsn && b < lsn + count)) return null;   // a short read, refused
            if (_cache is { } cached && cached.Lsn == lsn && cached.Data.Length == count * (S + (c2 ? 294 : 0)))
                return cached.Data;   // a stale answer, unless the reader made the drive read elsewhere first
            var per = S + (c2 ? 294 : 0);
            var o = new byte[count * per];
            for (var i = 0; i < count; i++)
            {
                var sector = lsn + i;
                var bad = Dead.Contains(sector) || BadReads.GetValueOrDefault(sector) > 0;
                if (BadReads.TryGetValue(sector, out var left) && left > 0) BadReads[sector] = left - 1;
                if (bad && Fails.Contains(sector)) return null;
                Disc.AsSpan(sector * S, S).CopyTo(o.AsSpan(i * per));
                if (!bad && SilentReads.TryGetValue(sector, out var quiet) && quiet > 0)
                {
                    SilentReads[sector] = quiet - 1;
                    o[i * per + 200] ^= 0x55;   // wrong, and the drive does not say so
                }
                if (bad)
                {
                    if (Dead.Contains(sector) && !SameGuess) o[i * per + 300] = (byte)++_guess;
                    o[i * per + 100] ^= 0xFF;
                    if (c2) o[i * per + S + 12] = 0x80;
                }
            }
            _cache = (lsn, o);
            return o;
        }
    }

    private static (byte[] Window, ReadReport Report) Read(FakeDrive drive, int sectors, int maxRetries = 20) =>
        new PassReader(drive).ReadSectors(0, sectors, 0, sectors, maxRetries);

    [Fact]
    public void A_clean_disc_reads_as_it_is_every_sector_clean()
    {
        var drive = new FakeDrive(100);
        var (window, report) = Read(drive, 100);
        Assert.Equal(drive.Disc, window);
        Assert.All(report.Sectors, s => Assert.Equal(SectorState.Clean, s));
        Assert.Equal(0, report.ReadErrors);
    }

    [Fact]
    public void A_sector_the_drive_flags_with_C2_is_read_again_until_two_clean_reads_agree()
    {
        var drive = new FakeDrive(100);
        drive.BadReads[37] = 3;
        var (window, report) = Read(drive, 100);
        Assert.Equal(drive.Disc, window);
        Assert.Equal(SectorState.Recovered, report.Sectors[37]);
        Assert.Equal(SectorState.Clean, report.Sectors[36]);
        Assert.Equal(0, report.Skips);
    }

    [Fact]
    public void A_run_that_fails_outright_is_read_sector_by_sector()
    {
        var drive = new FakeDrive(100);
        drive.BadReads[50] = 2;
        drive.Fails.Add(50);
        var (window, report) = Read(drive, 100);
        Assert.Equal(drive.Disc, window);
        Assert.Equal(SectorState.Recovered, report.Sectors[50]);
    }

    [Fact]
    public void A_sector_that_never_reads_clean_is_skipped_and_reported()
    {
        var drive = new FakeDrive(100);
        drive.Dead.Add(10);
        var damaged = new List<(int, SectorState)>();
        var (window, report) = new PassReader(drive).ReadSectors(0, 100, 0, 100, 5, damaged: (s, st) => damaged.Add((s, st)));
        Assert.Equal(SectorState.Skipped, report.Sectors[10]);
        Assert.Equal(1, report.Skips);
        Assert.Contains((10, SectorState.Skipped), damaged);
        Assert.Equal(drive.Disc.AsSpan(0, 10 * S).ToArray(), window.AsSpan(0, 10 * S).ToArray());   // the rest is still right
        Assert.Equal(drive.Disc.AsSpan(11 * S).ToArray(), window.AsSpan(11 * S).ToArray());
    }

    [Fact]
    public void Every_re_read_comes_from_the_disc_not_the_cache()
    {
        // Flagged in its run, then wrong once with no C2: served from the cache,
        // that wrong read would "agree" with itself and be taken as recovered.
        var drive = new FakeDrive(20_000);
        drive.BadReads[5] = 1;
        drive.SilentReads[5] = 1;
        var (window, report) = new PassReader(drive).ReadSectors(0, 24, 0, 24, 20);
        Assert.Equal(drive.Disc.AsSpan(0, 24 * S).ToArray(), window);
        Assert.Equal(SectorState.Recovered, report.Sectors[5]);
    }

    [Fact]
    public void A_drive_without_C2_still_reads_a_failed_run_sector_by_sector()
    {
        var drive = new FakeDrive(100) { C2Supported = false };
        drive.BadReads[60] = 1;
        drive.Fails.Add(60);
        var (window, report) = Read(drive, 100);
        Assert.Equal(drive.Disc, window);
        Assert.Equal(SectorState.Clean, report.Sectors[60]);   // the run failed; the sector alone read fine
    }

    [Fact]
    public void Sectors_outside_the_readable_range_stay_zero()
    {
        var drive = new FakeDrive(100);
        var (window, report) = new PassReader(drive).ReadSectors(-2, 50, 0, 50, 20);
        Assert.Equal(new byte[2 * S], window.AsSpan(0, 2 * S).ToArray());
        Assert.Equal(drive.Disc.AsSpan(0, 50 * S).ToArray(), window.AsSpan(2 * S).ToArray());
        Assert.Equal(SectorState.NotRead, report.Sectors[0]);
    }

    [Fact]
    public void A_damaged_sector_gets_at_most_five_tries_whatever_the_caller_asks()
    {
        var drive = new FakeDrive(100);
        drive.Dead.Add(10);
        var statuses = new List<RetryStatus>();
        new PassReader(drive).ReadSectors(0, 100, 0, 100, maxRetries: 20, retrying: statuses.Add);
        Assert.Equal(PassReader.MaxTries, drive.SingleReads[10]);
        Assert.Equal(PassReader.MaxTries, statuses.Count(st => st.Result is null));
        var last = statuses[^1];
        Assert.Equal((10, SectorState.Skipped, 1), (last.Sector, last.Result!.Value, last.Damaged));
    }

    [Fact]
    public void The_same_guess_three_times_ends_the_tries_early()
    {
        var drive = new FakeDrive(100) { SameGuess = true };
        drive.Dead.Add(10);
        new PassReader(drive).ReadSectors(0, 100, 0, 100, maxRetries: 5);
        Assert.Equal(3, drive.SingleReads[10]);
    }

    [Fact]
    public void A_recovered_sector_is_reported_as_recovered()
    {
        var drive = new FakeDrive(100);
        drive.BadReads[37] = 2;
        var statuses = new List<RetryStatus>();
        new PassReader(drive).ReadSectors(0, 100, 0, 100, 5, retrying: statuses.Add);
        Assert.Equal(SectorState.Recovered, statuses[^1].Result);
        Assert.Equal(37, statuses[^1].Sector);
        Assert.Equal(3, statuses[^1].Attempt);   // flagged once more, then two clean reads that agree
    }

    [Fact]
    public void A_run_cut_short_at_a_track_start_is_not_damage()
    {
        var drive = new FakeDrive(200);
        drive.TrackStarts.AddRange([50, 130]);
        var statuses = new List<RetryStatus>();
        var (window, report) = new PassReader(drive).ReadSectors(0, 200, 0, 200, 5, retrying: statuses.Add);
        Assert.Equal(drive.Disc, window);
        Assert.All(report.Sectors, st => Assert.Equal(SectorState.Clean, st));
        Assert.Equal(0, report.ReadErrors);
        Assert.Empty(statuses);
    }

    [Fact]
    public void A_sector_given_up_on_is_read_once_on_later_passes()
    {
        var drive = new FakeDrive(100);
        drive.Dead.Add(10);
        var reader = new PassReader(drive);
        reader.ReadSectors(0, 100, 0, 100, 5);
        Assert.Equal(PassReader.MaxTries, drive.SingleReads[10]);
        reader.ReadSectors(0, 100, 0, 100, 5);
        Assert.Equal(PassReader.MaxTries + 1, drive.SingleReads[10]);
    }

    [Fact]
    public void Damaged_sectors_are_read_again_slowly_then_back_to_normal_speed()
    {
        var drive = new FakeDrive(100);
        drive.BadReads[37] = 2;
        new PassReader(drive) { NormalSpeed = 8 }.ReadSectors(0, 100, 0, 100, 5);
        Assert.Equal([PassReader.RetrySpeed, 8], drive.Speeds);

        var capped = new FakeDrive(100);
        capped.BadReads[37] = 2;
        new PassReader(capped) { NormalSpeed = 2 }.ReadSectors(0, 100, 0, 100, 5);
        Assert.Empty(capped.Speeds);   // already slower than the retry speed: left alone
    }

    [Fact]
    public void Damage_at_the_very_end_still_leaves_the_drive_at_its_speed()
    {
        var drive = new FakeDrive(100);
        drive.BadReads[99] = 2;   // the last sector: no clean run after it
        new PassReader(drive) { NormalSpeed = null }.ReadSectors(0, 100, 0, 100, 5);
        Assert.Equal([PassReader.RetrySpeed, (int?)null], drive.Speeds);
    }
}
