using System.Buffers.Binary;

namespace Deadwax.Verify;

/// AccurateRip's per-track checksums, v1 and v2.
///
/// Each stereo sample is read as one little-endian 32-bit word (left in the low
/// half) and multiplied by its 1-based position in the track. v1 sums the low
/// 32 bits of each product; v2 sums the low and high halves, so it is not blind
/// to the right channel's top bits the way v1 is.
///
/// The first 5 sectors of the disc's first track and the last 5 sectors of its
/// last track are left out, because drives with different read offsets cannot
/// agree on them: the check starts at position 5 x 588 (2940) of track 1 and
/// stops at position count - 5 x 588 of the last track.
///
/// The start is exactly 2940, not the 2939 that some write-ups give: found on
/// 2026-09-24 when the live КОНЦЕРТ disc, whose track 1 has sound from sample
/// 2021, matched whipper only at 2940. Discs that open with a few seconds of
/// silence cannot tell the two apart, which is how it hid.
public static class AccurateRipChecksum
{
    private const int SkipSamples = 5 * 588;

    public static (uint V1, uint V2) Compute(ReadOnlySpan<byte> audio, bool firstTrack, bool lastTrack)
    {
        if (audio.Length % 4 != 0) throw new ArgumentException("Audio must be whole 16-bit stereo samples.", nameof(audio));

        var count = audio.Length / 4;
        long from = firstTrack ? SkipSamples : 0;
        long to = lastTrack ? count - SkipSamples : count;

        uint v1 = 0, v2 = 0;
        for (var i = 0; i < count; i++)
        {
            var position = (uint)(i + 1);
            if (position < from || position > to) continue;
            var sample = BinaryPrimitives.ReadUInt32LittleEndian(audio.Slice(i * 4, 4));
            var product = (ulong)sample * position;
            v1 += (uint)product;
            v2 += (uint)product + (uint)(product >> 32);
        }
        return (v1, v2);
    }
}
