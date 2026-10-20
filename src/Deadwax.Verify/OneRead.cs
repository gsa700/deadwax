namespace Deadwax.Verify;

/// When one read of a track is enough (spec §11, step 0, built 2026-10-20).
///
/// A track whose first read matches AccurateRip is proven by other people's
/// rips, more strongly than by a second read from the same drive, which may
/// come from the drive's cache ("Defeat audio cache: false"). Every other
/// track is read again, as before: no match, too few matching rips, a disc
/// AccurateRip does not have, or a read with skipped sectors.
///
/// The checksums leave out the first 5 sectors of the disc's first track and
/// the last 5 of its last (AccurateRipChecksum), so on those two tracks the
/// part AccurateRip cannot vouch for is read a second time and must come back
/// the same, byte for byte (EdgeBytes).
///
/// Checked before it was built against every log in his library: of 4149
/// tracks read twice, the 3992 that matched AccurateRip at confidence 2 or
/// more all had equal test and copy CRCs.
public static class OneRead
{
    /// Other people's rips that must agree: his call, a starting guess (2026-10-18).
    public const int DefaultConfidence = 2;

    /// The samples AccurateRip skips at the start of the first track (2939)
    /// and the end of the last (2940), in bytes, rounded up to 5 sectors of
    /// 588 stereo samples for both: an edge covers all of it, and one sample
    /// more at the start.
    public const int EdgeBytes = 5 * 588 * 4;

    public static bool Accepts(AccurateRipVerdict verdict, int skippedSectors, int minConfidence) =>
        skippedSectors == 0 && verdict.Confidence >= minConfidence;

    /// For the log: the version that matched and how many rips agree.
    public static string Proof(AccurateRipVerdict verdict) =>
        verdict.V2Confidence >= verdict.V1Confidence
            ? $"AccurateRip v2, confidence {verdict.V2Confidence}"
            : $"AccurateRip v1, confidence {verdict.V1Confidence}";
}
