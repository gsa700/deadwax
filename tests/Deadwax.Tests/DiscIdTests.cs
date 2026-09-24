using Deadwax.Drive;
using Deadwax.Metadata;
using Deadwax.Verify;

namespace Deadwax.Tests;

/// Gate G1, offline: the IDs Deadwax computes from a TOC equal the ones whipper
/// logged when it ripped the same disc. `deadwax check-logs` runs the same check
/// over the whole library; these golden logs keep it in the build.
public class DiscIdTests
{
    [Theory]
    [InlineData("52nd-street.log")]         // box disc, clean, in AccurateRip
    [InlineData("turnstiles.log")]          // matched a standalone reissue, not in AccurateRip
    [InlineData("anthology-of-bread.log")]  // 33 sectors before track 1, 20 tracks
    public void Ids_match_whipper(string file)
    {
        var log = Golden.Log(file);
        var toc = log.ToToc();

        Assert.Equal(log.MusicBrainzToc, DiscIds.MusicBrainzToc(toc));
        Assert.Equal(log.MusicBrainzId, DiscIds.MusicBrainz(toc));
        Assert.Equal(log.CddbId, DiscIds.Cddb(toc));
    }

    [Fact]
    public void Gap_before_track_1_is_not_a_track()
    {
        var log = Golden.Log("anthology-of-bread.log");
        Assert.True(log.HasPreTrackGap);

        var toc = log.ToToc();
        Assert.Equal(1, toc.FirstTrack);
        Assert.Equal(20, toc.Tracks.Count);
        Assert.Equal(33, toc.Tracks[0].StartLsn);
    }

    [Fact]
    public void AccurateRip_id_names_the_file_holding_whippers_checksums()
    {
        var log = Golden.Log("52nd-street.log");
        var toc = log.ToToc();
        var id = AccurateRipId.From(toc, DiscIds.Cddb(toc));

        Assert.Equal("dBAR-009-000df00d-00671c3d-7c098609.bin", id.FileName);
        Assert.Equal("http://www.accuraterip.com/accuraterip/d/0/0/dBAR-009-000df00d-00671c3d-7c098609.bin", id.Url);

        var blocks = AccurateRipDatabase.Parse(Golden.Bytes("52nd-street.bin"));
        Assert.NotEmpty(blocks);
        Assert.All(blocks, b => Assert.Equal((id.Id1, id.Id2, id.Cddb), (b.Id1, b.Id2, b.Cddb)));

        foreach (var track in log.Tracks)
        {
            var v2 = track.V2!;
            Assert.True(v2.IsMatch);
            Assert.Contains(blocks, b => b.Tracks[track.Number - 1].Crc == v2.RemoteCrc);
        }
    }

    [Fact]
    public void Enhanced_cd_ids_stop_before_the_data_session()
    {
        // Two audio tracks, then a data track in a second session.
        var toc = new Toc(
            [new TocTrack(1, 0, true), new TocTrack(2, 20000, true), new TocTrack(3, 60000, false)],
            leadoutLsn: 90000);

        Assert.Equal(2, toc.IdTracks.Count);
        Assert.Equal(60000 - Toc.SessionGap, toc.AudioLeadoutLsn);
        Assert.StartsWith("1+2+", DiscIds.MusicBrainzToc(toc));
        Assert.Equal(2, AccurateRipId.From(toc, 0).AudioTracks);
        // CDDB describes the whole disc, data track included.
        Assert.Equal(3u, DiscIds.Cddb(toc) & 0xFF);
    }

    [Fact]
    public void Toc_rejects_tracks_out_of_order()
    {
        Assert.Throws<ArgumentException>(() => new Toc([new TocTrack(1, 100, true), new TocTrack(2, 50, true)], 200));
        Assert.Throws<ArgumentException>(() => new Toc([new TocTrack(1, 0, true), new TocTrack(3, 50, true)], 200));
        Assert.Throws<ArgumentException>(() => new Toc([new TocTrack(1, 0, true)], 0));
    }
}
