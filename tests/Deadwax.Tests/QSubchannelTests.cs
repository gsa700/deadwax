using Deadwax.Drive;

namespace Deadwax.Tests;

/// The Q-subchannel decoder against frames built the way a disc carries them.
public class QSubchannelTests
{
    [Fact]
    public void A_position_frame_decodes_with_its_crc()
    {
        var q = QFrame.EncodePosition(control: 0x2, track: 7, index: 1, relativeLsn: 75 * 61 + 3, absoluteLsn: 123_456);
        var f = QFrame.Parse(q);
        Assert.True(f.CrcOk);
        Assert.True(f.Consistent);
        Assert.Equal(1, f.Mode);
        Assert.Equal(7, f.Track);
        Assert.Equal(1, f.Index);
        Assert.Equal((1, 1, 3), (f.Min, f.Sec, f.Frame));
        Assert.Equal(123_456, f.AbsoluteLsn);
        Assert.True(f.CopyPermitted);
        Assert.False(f.PreEmphasis);
    }

    [Fact]
    public void A_flipped_bit_fails_the_crc()
    {
        var q = QFrame.EncodePosition(0, 3, 0, 10, 20);
        q[4] ^= 0x10;
        Assert.False(QFrame.Parse(q).CrcOk);
    }

    [Fact]
    public void The_crc_may_come_back_as_computed_or_inverted()
    {
        var q = QFrame.EncodePosition(0, 3, 1, 10, 20);
        Assert.True(QFrame.Parse(q).CrcOk);
        var crc = QFrame.Crc16(q.AsSpan(0, 10));
        q[10] = (byte)(crc >> 8); q[11] = (byte)crc;
        Assert.True(QFrame.Parse(q).CrcOk);
    }

    [Fact]
    public void Catalog_and_isrc_frames_decode()
    {
        var mcn = QFrame.Parse(QFrame.EncodeCatalog("0082876858952", 42));
        Assert.True(mcn.Trusted);
        Assert.Equal(2, mcn.Mode);
        Assert.Equal("0082876858952", mcn.Mcn);

        var isrc = QFrame.Parse(QFrame.EncodeIsrc("USSM18300106", 7));
        Assert.True(isrc.Trusted);
        Assert.Equal(3, isrc.Mode);
        Assert.Equal("USSM18300106", isrc.Isrc);

        var letters = QFrame.Parse(QFrame.EncodeIsrc("GBAYE0601498", 0));
        Assert.Equal("GBAYE0601498", letters.Isrc);
    }

    [Fact]
    public void Nonsense_is_not_consistent()
    {
        var q = QFrame.EncodePosition(0, 0, 1, 0, 0);   // track 0 is the lead-in, never a track
        Assert.False(QFrame.Parse(q).Consistent);
        var bad = QFrame.EncodePosition(0, 5, 1, 0, 0);
        bad[4] = 0x7A;                                   // "7A" is not two BCD digits
        var crc = (ushort)~QFrame.Crc16(bad.AsSpan(0, 10));
        bad[10] = (byte)(crc >> 8); bad[11] = (byte)crc;
        var f = QFrame.Parse(bad);
        Assert.True(f.CrcOk);
        Assert.False(f.Consistent);
    }

    [Fact]
    public void Q_comes_out_of_a_raw_pw_block()
    {
        var q = QFrame.EncodeIsrc("USSM18300106", 7);
        var pw = new byte[96];
        for (var i = 0; i < 96; i++)
        {
            if ((q[i / 8] & (0x80 >> (i % 8))) != 0) pw[i] |= 0x40;
            pw[i] |= 0x80;                               // P flag set throughout, must not leak into Q
            pw[i] |= (byte)(i % 2 == 0 ? 0x20 : 0x01);   // R and W noise
        }
        Assert.Equal(q, QFrame.QFromRawPw(pw));
    }
}
