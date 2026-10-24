namespace Deadwax.Drive;

/// "Make best copy" (spec §8, his call 2026-10-24): for a track no two reads
/// of which agree, the audio the reads agree on most, instead of whichever
/// read came last. Not a verified copy, and never called one, unless the
/// result then matches AccurateRip.
///
/// Sector by sector (2352 bytes from the track's start): where most reads
/// agree on the whole sector, that sector; where they do not, sample by
/// sample (16 bits), the value most reads gave; where no value has a
/// majority, the read whose sector was best: clean, then recovered, then one
/// the reader gave up on (Deadwax's own reader keeps the drive's own
/// concealment there, the read with the fewest C2 flags: the Led Zeppelin
/// reissue's lesson), the later read when two are as good.
///
/// On Dido's Life for Rent, track 11 (a defect in the last minute), single
/// reads came 18 to 75 ms of audio away from the verified track; a vote of
/// three of them, 27 ms.
public static class BestCopy
{
    public const int SectorBytes = 2352;

    public sealed record Result(byte[] Audio, int Sectors, int Unanimous, int Voted, int NoMajority);

    /// reads: the track's passes, all the same length. state: how good read r's
    /// sector holding byte b was (Clean > Recovered > Skipped), or null when
    /// not known (every read then as good, the later one winning a tie).
    public static Result Vote(IReadOnlyList<byte[]> reads, Func<int, int, SectorState>? state = null)
    {
        if (reads.Count == 0) throw new ArgumentException("No reads to vote on.", nameof(reads));
        var length = reads[0].Length;
        if (reads.Any(r => r.Length != length)) throw new ArgumentException("The reads are not the same length.", nameof(reads));
        var audio = new byte[length];
        int sectors = 0, unanimous = 0, voted = 0, noMajority = 0;

        for (var at = 0; at < length; at += SectorBytes)
        {
            sectors++;
            var size = Math.Min(SectorBytes, length - at);
            var target = audio.AsSpan(at, size);

            // The largest group of reads that agree on the whole sector.
            var (best, count) = (0, 0);
            for (var r = 0; r < reads.Count; r++)
            {
                var same = 0;
                for (var q = 0; q < reads.Count; q++)
                    if (reads[q].AsSpan(at, size).SequenceEqual(reads[r].AsSpan(at, size))) same++;
                if (same > count) (best, count) = (r, same);
            }
            if (count == reads.Count) { reads[best].AsSpan(at, size).CopyTo(target); unanimous++; continue; }
            if (count * 2 > reads.Count) { reads[best].AsSpan(at, size).CopyTo(target); voted++; continue; }

            // Sample by sample, and where a sample has no majority, the best read of the sector.
            var fallback = BestRead(reads.Count, at, state);
            var anyWithout = false;
            for (var i = 0; i + 1 < size; i += 2)
            {
                var (value, votes) = ((short)0, 0);
                for (var r = 0; r < reads.Count; r++)
                {
                    var v = BitConverter.ToInt16(reads[r], at + i);
                    var c = 0;
                    for (var q = 0; q < reads.Count; q++) if (BitConverter.ToInt16(reads[q], at + i) == v) c++;
                    if (c > votes) (value, votes) = (v, c);
                }
                if (votes * 2 <= reads.Count)
                {
                    value = BitConverter.ToInt16(reads[fallback], at + i);
                    anyWithout = true;
                }
                BitConverter.TryWriteBytes(target[i..], value);
            }
            if (anyWithout) noMajority++; else voted++;
        }
        return new Result(audio, sectors, unanimous, voted, noMajority);
    }

    private static int BestRead(int count, int at, Func<int, int, SectorState>? state)
    {
        var best = count - 1;
        if (state is null) return best;
        for (var r = count - 1; r >= 0; r--)
            if (Rank(state(r, at)) > Rank(state(best, at))) best = r;
        return best;
    }

    private static int Rank(SectorState s) => s switch
    {
        SectorState.Clean => 3,
        SectorState.Recovered => 2,
        SectorState.Skipped => 1,
        _ => 0,
    };
}
