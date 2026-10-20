using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Output;
using Deadwax.Verify;

namespace Deadwax.Tests;

/// Spec §11 step 0: a track whose one read matches AccurateRip is read once.
public class OneReadTests
{
    private static readonly Toc FiftySecondStreet = Golden.Log("52nd-street.log").ToToc();

    [Theory]
    [InlineData(0, 2, 0, true)]     // two other rips agree (v2)
    [InlineData(107, 0, 0, true)]   // v1 alone is enough
    [InlineData(1, 0, 0, false)]    // one rip is below the threshold
    [InlineData(0, 0, 0, false)]    // no match
    [InlineData(0, 61, 1, false)]   // a skipped sector is a guess: read it again
    public void Accepts_a_match_with_enough_rips_and_nothing_skipped(int v1, int v2, int skips, bool accepted) =>
        Assert.Equal(accepted, OneRead.Accepts(new AccurateRipVerdict(1, 0, 0, v1, v2), skips, OneRead.DefaultConfidence));

    [Fact]
    public void Proof_names_the_version_that_matched_more()
    {
        Assert.Equal("AccurateRip v2, confidence 61", OneRead.Proof(new AccurateRipVerdict(1, 0, 0, 0, 61)));
        Assert.Equal("AccurateRip v1, confidence 9", OneRead.Proof(new AccurateRipVerdict(1, 0, 0, 9, 3)));
    }

    [Fact]
    public void The_edges_cover_everything_AccurateRip_leaves_out()
    {
        // Silence, then a loud sample at each boundary: a sample the checksum
        // skips must lie inside an edge, and the edges reach no further than
        // one sample past what is skipped (2939 at the start, 2940 at the end).
        var audio = new byte[200 * 588 * 4];
        var before = AccurateRipChecksum.Compute(audio, true, true);
        audio[OneRead.EdgeBytes - 8] = 0x7f;                // sample 2938, the last one skipped at the start
        audio[audio.Length - OneRead.EdgeBytes] = 0x7f;     // the first one skipped at the end
        Assert.Equal(before, AccurateRipChecksum.Compute(audio, true, true));

        var start = (byte[])audio.Clone();
        start[OneRead.EdgeBytes - 4] = 0x7f;                // sample 2939: counted, and still inside the edge
        Assert.NotEqual(before, AccurateRipChecksum.Compute(start, true, true));
        var end = (byte[])audio.Clone();
        end[audio.Length - OneRead.EdgeBytes - 4] = 0x7f;   // the sample before the end edge: counted
        Assert.NotEqual(before, AccurateRipChecksum.Compute(end, true, true));
    }

    [Theory]
    [InlineData(1, 667)]
    [InlineData(2, 667)]
    [InlineData(9, 667)]    // the last track: part of its window lies past the audio
    [InlineData(1, -30)]    // the first track starts before the disc
    [InlineData(3, 0)]
    public void A_part_cuts_the_same_bytes_as_the_whole_track(int number, int offset)
    {
        var w = ReadWindow.For(FiftySecondStreet, FiftySecondStreet.Track(number), offset);
        // A fake disc: each readable byte is a function of its absolute position.
        byte At(long sector, int i) => (byte)(sector * 31 + i * 7);
        byte[] Window(ReadWindow r)
        {
            var bytes = new byte[(long)(r.EndSector - r.FirstSector) * ReadWindow.SectorBytes];
            for (var s = r.ReadableStart; s < r.ReadableEnd; s++)
                for (var i = 0; i < ReadWindow.SectorBytes; i++)
                    bytes[(long)(s - r.FirstSector) * ReadWindow.SectorBytes + i] = At(s, i);
            return bytes;
        }
        var whole = Window(w).AsSpan(w.TrackStartInWindow, w.TrackBytes).ToArray();

        foreach (var (from, to) in new[] { (0, OneRead.EdgeBytes), (w.TrackBytes - OneRead.EdgeBytes, w.TrackBytes), (1000, 5000) })
        {
            var p = w.Part(from, to);
            Assert.True(p.EndSector - p.FirstSector <= OneRead.EdgeBytes / ReadWindow.SectorBytes + 2);
            Assert.Equal(whole.AsSpan(from, to - from).ToArray(), Window(p).AsSpan(p.TrackStartInWindow, p.TrackBytes).ToArray());
        }
    }

    [Fact]
    public void A_track_read_once_logs_no_copy_and_says_how_it_was_proven()
    {
        var whipper = Golden.Log("52nd-street.log");
        var toc = whipper.ToToc();
        var disc = new LogDisc(
            "PIONEER BD-RW   BDR-209D (revision 1.10)", "libcdio-paranoia 10.2+2.0.2 (libcdio 2.3.0)", 667, "Deadwax", false,
            "Billy Joel", "52nd Street", DiscIds.Cddb(toc), DiscIds.MusicBrainz(toc), DiscIds.MusicBrainzAttachUrl(toc), null, toc);
        var tracks = whipper.Tracks.Select(t => new LogTrack(
            t.Number, $"x/{t.Number:D2}.flac", 1.0, false, 9.5,
            t.CopyCrc!.Value, t.Number == 5 ? t.CopyCrc : null, 0, 0,
            new LogAccurateRip(false, 0, 0, null),
            new LogAccurateRip(true, t.V2!.LocalCrc!.Value, t.V2.Confidence!.Value, t.V2.RemoteCrc),
            "Copy OK",
            t.Number == 5 ? "2 (test and copy CRCs match)" : $"1 (AccurateRip v2, confidence {t.V2.Confidence})",
            ReadOnce: t.Number != 5)).ToList();

        var text = RipLog.Write(disc, tracks, "0.5.0", DateTimeOffset.Parse("2026-10-20T12:00:00Z"));
        Assert.True(RipLog.Verify(text));
        Assert.Equal(1, text.Split("Copy CRC:").Length - 1);              // only track 5 had a copy
        Assert.Contains("    Reads: 2 (test and copy CRCs match)\n    Status: Copy OK", text);
        Assert.Contains($"  Reads: {tracks.Count - 1} of {tracks.Count} tracks read once, proven by AccurateRip; 1 read twice or more", text);
        Assert.Contains("Health status: No errors occurred", text);

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text);
            var back = WhipperLog.Load(path);
            // The file's CRC is the one read's when there was no copy.
            Assert.Equal(whipper.Tracks.Select(t => t.CopyCrc), back.Tracks.Select(t => t.AudioCrc));
            Assert.Null(back.Tracks.First(t => t.Number == 1).CopyCrc);
            Assert.All(back.Tracks, t => Assert.Equal("Copy OK", t.Status));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_log_without_reads_lines_is_written_as_before()
    {
        var whipper = Golden.Log("52nd-street.log");
        var toc = whipper.ToToc();
        var disc = new LogDisc("d", "e", 667, "g", false, "a", "b", 0, "id", "url", null, toc);
        var tracks = whipper.Tracks.Select(t => new LogTrack(t.Number, "f.flac", 1.0, false, 9.5, 1, 1, 0, 0,
            new LogAccurateRip(false, 0, 0, null), new LogAccurateRip(false, 0, 0, null), "Copy OK")).ToList();
        var text = RipLog.Write(disc, tracks, "0.4.6", DateTimeOffset.Parse("2026-10-20T12:00:00Z"));
        Assert.DoesNotContain("Reads:", text);
        Assert.Equal(tracks.Count, text.Split("Copy CRC:").Length - 1);
    }
}
