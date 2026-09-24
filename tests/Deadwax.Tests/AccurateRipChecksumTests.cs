using Deadwax.Verify;

namespace Deadwax.Tests;

public class AccurateRipChecksumTests
{
    private static byte[] Samples(int count, uint value)
    {
        var bytes = new byte[count * 4];
        for (var i = 0; i < count; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), value);
        return bytes;
    }

    private static uint SumPositions(long from, long to) => (uint)((from + to) * (to - from + 1) / 2);

    [Fact]
    public void Middle_track_weights_every_sample_by_position()
    {
        var (v1, v2) = AccurateRipChecksum.Compute(Samples(6000, 1), firstTrack: false, lastTrack: false);
        Assert.Equal(SumPositions(1, 6000), v1);
        Assert.Equal(v1, v2);   // products this small have no high half
    }

    [Fact]
    public void First_and_last_tracks_skip_five_sectors()
    {
        Assert.Equal(SumPositions(5 * 588, 6000), AccurateRipChecksum.Compute(Samples(6000, 1), true, false).V1);
        Assert.Equal(SumPositions(1, 6000 - 5 * 588), AccurateRipChecksum.Compute(Samples(6000, 1), false, true).V1);
    }

    [Fact]
    public void V2_adds_the_high_half_of_each_product()
    {
        var (v1, v2) = AccurateRipChecksum.Compute(Samples(2, 0xFFFFFFFF), false, false);
        // 0xFFFFFFFF x 1 + 0xFFFFFFFF x 2: low halves 0xFFFFFFFF + 0xFFFFFFFE, high halves 0 + 1.
        Assert.Equal(0xFFFFFFFDu, v1);
        Assert.Equal(0xFFFFFFFEu, v2);
    }

    [Fact]
    public void Verdict_takes_the_best_matching_entry()
    {
        var blocks = AccurateRipDatabase.Parse(Golden.Bytes("52nd-street.bin"));
        var v = AccurateRipMatch.For(blocks, 0, 1, v1: 0x12345678, v2: 0x8F0B465F);  // whipper's v2 for track 1
        Assert.True(v.IsAccurate);
        Assert.Equal(0, v.V1Confidence);
        Assert.True(v.V2Confidence >= 20);
    }
}
