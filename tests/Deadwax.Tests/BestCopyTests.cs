using Deadwax.Drive;

namespace Deadwax.Tests;

/// "Make best copy" (spec §8): the vote of a track's reads.
public class BestCopyTests
{
    private const int S = BestCopy.SectorBytes;

    private static byte[] Track(int sectors) => Enumerable.Range(0, sectors * S).Select(i => (byte)(i * 13 + i / S)).ToArray();

    private static byte[] With(byte[] track, params (int Byte, byte Value)[] changes)
    {
        var copy = track.ToArray();
        foreach (var (b, v) in changes) copy[b] = v;
        return copy;
    }

    [Fact]
    public void Reads_that_agree_give_themselves()
    {
        var t = Track(10);
        var r = BestCopy.Vote([t, t.ToArray(), t.ToArray()]);
        Assert.Equal(t, r.Audio);
        Assert.Equal((10, 10, 0, 0), (r.Sectors, r.Unanimous, r.Voted, r.NoMajority));
    }

    [Fact]
    public void A_sector_wrong_in_one_read_of_three_is_outvoted()
    {
        var t = Track(10);
        var r = BestCopy.Vote([With(t, (3 * S + 100, 0)), t.ToArray(), t.ToArray()]);
        Assert.Equal(t, r.Audio);
        Assert.Equal(1, r.Voted);
    }

    [Fact]
    public void Different_samples_wrong_in_each_read_are_voted_sample_by_sample()
    {
        // Every read wrong somewhere in sector 5, never in the same sample.
        var t = Track(10);
        var r = BestCopy.Vote([With(t, (5 * S + 10, 1)), With(t, (5 * S + 500, 2)), With(t, (5 * S + 2000, 3))]);
        Assert.Equal(t, r.Audio);
        Assert.Equal((1, 0), (r.Voted, r.NoMajority));
    }

    [Fact]
    public void With_no_majority_the_best_read_of_the_sector_is_taken()
    {
        var t = Track(4);
        var a = With(t, (2 * S + 40, 1));
        var b = With(t, (2 * S + 40, 2));
        // Read 0 recovered that sector, read 1 gave up on it: read 0's value, though read 1 is later.
        var r = BestCopy.Vote([a, b], (read, at) => at / S == 2 && read == 1 ? SectorState.Skipped : SectorState.Recovered);
        Assert.Equal(1, r.Audio[2 * S + 40]);
        Assert.Equal(1, r.NoMajority);
        // As good as each other: the later read.
        Assert.Equal(2, BestCopy.Vote([a, b]).Audio[2 * S + 40]);
    }

    [Fact]
    public void Reads_of_different_lengths_are_refused() =>
        Assert.Throws<ArgumentException>(() => BestCopy.Vote([Track(2), Track(3)]));
}
