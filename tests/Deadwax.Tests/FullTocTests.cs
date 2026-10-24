using Deadwax.Drive;

namespace Deadwax.Tests;

/// FullToc, step 3's TOC reader (spec §11), on answers laid out as MMC's
/// READ TOC format 2 has them.
public class FullTocTests
{
    private static byte[] Answer(params (int Session, int Control, int Point, int Lsn)[] entries)
    {
        var body = new List<byte> { 1, (byte)entries.Max(e => e.Session) };
        foreach (var (session, ctl, point, lsn) in entries)
        {
            var f = lsn + 150;
            body.AddRange([(byte)session, (byte)(0x10 | ctl), 0, (byte)point, 0, 0, 0, 0,
                (byte)(f / 75 / 60), (byte)(f / 75 % 60), (byte)(f % 75)]);
        }
        var length = body.Count;
        return [(byte)(length >> 8), (byte)length, .. body];
    }

    [Fact]
    public void An_audio_cd_reads_as_its_tracks_and_lead_out()
    {
        var (toc, control) = FullToc.Parse(Answer(
            (1, 0, 0xA0, 0), (1, 0, 0xA1, 0), (1, 0, 0xA2, 193952 - 150 + 150),
            (1, 0, 1, 0), (1, 2, 2, 21300), (1, 1, 3, 38000)));
        Assert.Equal([1, 2, 3], toc.Tracks.Select(t => t.Number));
        Assert.Equal([0, 21300, 38000], toc.Tracks.Select(t => t.StartLsn));
        Assert.All(toc.Tracks, t => Assert.True(t.IsAudio));
        Assert.Equal(193952, toc.LeadoutLsn);
        Assert.Equal(2, control[2]);   // copy permitted
        Assert.Equal(1, control[3]);   // pre-emphasis
    }

    [Fact]
    public void An_enhanced_cd_ends_at_the_last_sessions_lead_out_with_its_data_track()
    {
        var (toc, _) = FullToc.Parse(Answer(
            (1, 0, 0xA2, 150000), (1, 0, 1, 0), (1, 0, 2, 80000),
            (2, 4, 0xA2, 300000), (2, 4, 3, 161400)));
        Assert.Equal(300000, toc.LeadoutLsn);
        Assert.False(toc.Tracks[2].IsAudio);
        Assert.Equal(2, toc.IdTracks.Count(t => t.IsAudio));
    }

    [Fact]
    public void Entries_that_are_not_track_starts_are_skipped()
    {
        var answer = Answer((1, 0, 0xA2, 50000), (1, 0, 1, 0));
        answer[4 + 11 + 1] = 0x50;   // the track entry made ADR 5 (not a position)
        Assert.Throws<DriveException>(() => FullToc.Parse(answer));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 2, 1, 1 })]
    public void An_answer_with_no_tracks_is_a_drive_error(byte[] answer) =>
        Assert.Throws<DriveException>(() => FullToc.Parse(answer));

    [Fact]
    public void A_toc_that_does_not_hold_together_is_a_drive_error()
    {
        Assert.Throws<DriveException>(() => FullToc.Parse(Answer((1, 0, 0xA2, 100), (1, 0, 1, 0), (1, 0, 2, 5000))));   // lead-out before track 2
    }
}
