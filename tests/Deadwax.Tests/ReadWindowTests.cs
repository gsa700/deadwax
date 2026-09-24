using Deadwax.Drive;
using Deadwax.Verify;

namespace Deadwax.Tests;

public class ReadWindowTests
{
    // 52nd Street as the drive reports it.
    private static readonly Toc FiftySecondStreet = Golden.Log("52nd-street.log").ToToc();

    [Fact]
    public void Positive_offset_reads_one_sector_more_and_cuts_the_track_out()
    {
        var track = FiftySecondStreet.Track(2);                       // 18328..35888
        var w = ReadWindow.For(FiftySecondStreet, track, 667);

        // +667 samples = 1 sector (588) + 79 samples.
        Assert.Equal(18328 + 1, w.FirstSector);
        Assert.Equal(35888 + 2, w.EndSector);
        Assert.Equal(79 * 4, w.TrackStartInWindow);
        Assert.Equal((35888 - 18328) * 2352, w.TrackBytes);
        Assert.Equal((w.FirstSector, w.EndSector), (w.ReadableStart, w.ReadableEnd));
    }

    [Fact]
    public void Past_the_end_of_the_disc_is_not_read()
    {
        var last = FiftySecondStreet.Track(9);
        var w = ReadWindow.For(FiftySecondStreet, last, 667);

        Assert.Equal(182921 + 2, w.EndSector);
        Assert.Equal(182921, w.ReadableEnd);   // the lead-out: the rest stays zero
    }

    [Fact]
    public void Negative_offset_starts_before_the_disc_on_track_1()
    {
        var w = ReadWindow.For(FiftySecondStreet, FiftySecondStreet.Track(1), -30);

        Assert.Equal(-1, w.FirstSector);
        Assert.Equal(0, w.ReadableStart);
        Assert.Equal((588 - 30) * 4, w.TrackStartInWindow);
    }

    [Fact]
    public void Zero_offset_is_exactly_the_track()
    {
        var track = FiftySecondStreet.Track(3);
        var w = ReadWindow.For(FiftySecondStreet, track, 0);

        Assert.Equal(track.StartLsn, w.FirstSector);
        Assert.Equal(FiftySecondStreet.EndLsn(track), w.EndSector);
        Assert.Equal(0, w.TrackStartInWindow);
    }
}

public class AudioCheckTests
{
    [Fact]
    public void Crc32_is_zlibs()
    {
        // The standard check value for CRC-32/ISO-HDLC, which zlib computes.
        Assert.Equal(0xCBF43926u, AudioChecks.Crc32("123456789"u8));
    }

    [Fact]
    public void Offset_comes_from_whippers_drive_section()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                [drive:PIONEER%20%3ABD-RW%20%20%20BDR-209D%3A1.10]
                vendor = PIONEER
                model = BD-RW   BDR-209D
                release = 1.10
                defeats_cache = False
                read_offset = 667

                [whipper.cd.rip]
                track_template = %%A/%%y - %%d/%%A - %%t - %%n
                """);

            // libcdio pads the model differently; spacing must not matter.
            Assert.Equal(667, WhipperConfig.ReadOffset("PIONEER", "BD-RW BDR-209D", "1.10", path));
            Assert.Null(WhipperConfig.ReadOffset("PIONEER", "BD-RW BDR-209D", "1.00", path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
