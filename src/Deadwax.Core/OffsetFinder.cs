using Deadwax.Metadata;
using Deadwax.Drive;
using Deadwax.Verify;

namespace Deadwax.Core;

/// One offset that AccurateRip agreed with, and how many rips agreed.
public sealed record OffsetCandidate(int Offset, int Confidence, int Track);

public sealed record OffsetSearch(
    DriveIdentity? Drive, string DiscId, int Track, int Pressings, IReadOnlyList<OffsetCandidate> Candidates)
{
    /// The offset to use: exactly one candidate. Several means other
    /// pressings of the same disc matched too (see OffsetFinder), and
    /// confidence cannot pick between them; another disc can.
    public OffsetCandidate? Best => Candidates.Count == 1 ? Candidates[0] : null;

    /// The offsets this disc and another both allow. Pressing shifts differ
    /// from album to album; the drive's offset does not.
    public OffsetSearch Intersect(OffsetSearch other)
    {
        var theirs = other.Candidates.Select(c => c.Offset).ToHashSet();
        return this with { Candidates = Candidates.Where(c => theirs.Contains(c.Offset)).ToList() };
    }
}

/// Measures a drive's read offset from a disc AccurateRip knows, as whipper's
/// `offset find` does and EAC's drive detection did before it. The drive hands
/// back audio shifted by a fixed number of samples; AccurateRip's checksums
/// were made from correctly aligned audio by thousands of other drives. So:
/// read one track once, with a few sectors of margin on each side, then for
/// every offset in the range slide the track window by that many samples,
/// take the AccurateRip checksums of what is in it, and see which offset
/// makes them match the database. One read, the rest is arithmetic.
///
/// The disc must be a well-known pressed CD (in the database, several
/// submissions); a middle track is used so the first/last-track special cases
/// in the checksum do not apply, and the shortest one, since every offset in
/// the range costs a pass over the whole track.
///
/// ONE DISC IS OFTEN NOT ENOUGH, found on the first run (Now and Zen,
/// 2026-10-05): the database holds several PRESSINGS of a disc, and a pressing
/// whose audio sits a few samples from this one's matches at the drive's
/// offset plus that shift. +667 (21 rips, this pressing) came with +679 (88),
/// +673 (83) and +1343 (37) from other pressings, and confidence cannot say
/// which is the drive. The caller then measures a second disc and keeps the
/// offsets both allow (OffsetSearch.Intersect): pressing shifts differ from
/// album to album, the drive does not.
public static class OffsetFinder
{
    public const int Range = 2000;   // samples either way; every drive in the AccurateRip list is inside

    public static async Task<OffsetSearch> FindAsync(
        string? device, Action<string> say, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        device ??= CdDrive.DefaultDevice;
        DriveIdentity? identity;
        Toc toc;
        using (var drive = CdDrive.Open(device))
        {
            identity = drive.ReadIdentity();
            toc = drive.ReadToc();
        }
        var discId = DiscIds.MusicBrainz(toc);
        var audio = toc.IdTracks.Where(t => t.IsAudio).ToList();
        if (audio.Count < 3)
            throw new RipException("Use a disc with at least three audio tracks; the measurement needs one that is neither first nor last.");

        say($"Drive {identity?.ToString() ?? device}, disc {discId}: asking AccurateRip...");
        IReadOnlyList<AccurateRipBlock>? blocks;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Deadwax/{RipSession.Version}");
            blocks = await AccurateRipDatabase.FetchAsync(http, AccurateRipId.From(toc, DiscIds.Cddb(toc)), ct);
        }
        if (blocks is null || blocks.Count == 0)
            throw new RipException("AccurateRip does not know this disc, so it cannot tell which offset is right. Use a well-known commercial CD.");

        // The shortest track that is neither first nor last.
        var inner = audio.Skip(1).Take(audio.Count - 2).ToList();
        var track = inner.MinBy(t => toc.EndLsn(t) - t.StartLsn)!;
        var index = audio.IndexOf(track);
        var trackBytes = (long)(toc.EndLsn(track) - track.StartLsn) * ReadWindow.SectorBytes;
        say($"Reading track {track.Number} ({(toc.EndLsn(track) - track.StartLsn) / Toc.SectorsPerSecond} s) with a margin on each side...");

        // Enough sectors on each side to slide Range samples either way.
        var margin = Range * 4 / ReadWindow.SectorBytes + 2;
        var first = track.StartLsn - margin;
        var end = toc.EndLsn(track) + margin;
        byte[] window;
        using (var reader = SecureReader.Open(device))
            (window, _) = reader.ReadSectors(first, end, Math.Max(first, 0), Math.Min(end, toc.AudioLeadoutLsn),
                progress: progress, ct: ct);

        say($"Checking {2 * Range + 1} offsets against {blocks.Count} pressing(s)...");
        var trackStart = (long)(track.StartLsn - first) * ReadWindow.SectorBytes;
        var found = new List<OffsetCandidate>();
        var gate = new object();
        Parallel.For(-Range, Range + 1, new ParallelOptions { CancellationToken = ct }, offset =>
        {
            var start = (int)(trackStart + (long)offset * 4);
            var (v1, v2) = AccurateRipChecksum.Compute(window.AsSpan(start, (int)trackBytes), false, false);
            var verdict = AccurateRipMatch.For(blocks, index, track.Number, v1, v2);
            if (!verdict.IsAccurate) return;
            lock (gate) found.Add(new OffsetCandidate(offset, verdict.Confidence, track.Number));
        });
        found.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        return new OffsetSearch(identity, discId, track.Number, blocks.Count, found);
    }
}
