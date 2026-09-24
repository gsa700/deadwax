using System.Security.Cryptography;
using System.Text;
using Deadwax.Drive;

namespace Deadwax.Metadata;

/// The disc IDs MusicBrainz and freedb/CDDB derive from a TOC.
///
/// Both methods were first rebuilt in Python for whipper-wizard (MusicBrainz on
/// 2026-08-27, CDDB on 2026-08-26) and validated against whipper's own logs;
/// this is the same arithmetic, checked against all of them again by
/// `deadwax check-logs`.
public static class DiscIds
{
    /// MusicBrainz disc ID: SHA-1 over the first and last track numbers and 100
    /// offset slots (slot 0 the lead-out, then each track, the rest zero), all as
    /// upper-case hex, then base64 with "+/=" swapped for "._-" so it fits a URL.
    /// https://musicbrainz.org/doc/Disc_ID_Calculation
    public static string MusicBrainz(Toc toc)
    {
        var tracks = toc.IdTracks;
        var text = new StringBuilder(2 + 2 + 100 * 8);
        text.Append(tracks[0].Number.ToString("X2"));
        text.Append(tracks[^1].Number.ToString("X2"));
        text.Append((toc.AudioLeadoutLsn + Toc.LeadIn).ToString("X8"));
        for (var slot = 1; slot < 100; slot++)
        {
            var track = tracks.FirstOrDefault(t => t.Number == slot);
            text.Append((track is null ? 0 : track.StartLsn + Toc.LeadIn).ToString("X8"));
        }

        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(text.ToString()));
        return Convert.ToBase64String(hash).Replace('+', '.').Replace('/', '_').Replace('=', '-');
    }

    /// The TOC as MusicBrainz's lookup and attach URLs spell it:
    /// "first+last+leadout+offset1+offset2...", every position including the lead-in.
    public static string MusicBrainzToc(Toc toc)
    {
        var tracks = toc.IdTracks;
        var parts = new List<int> { tracks[0].Number, tracks[^1].Number, toc.AudioLeadoutLsn + Toc.LeadIn };
        parts.AddRange(tracks.Select(t => t.StartLsn + Toc.LeadIn));
        return string.Join('+', parts);
    }

    public static string MusicBrainzAttachUrl(Toc toc) =>
        $"https://musicbrainz.org/cdtoc/attach?toc={MusicBrainzToc(toc)}&tracks={toc.IdTracks.Count}&id={MusicBrainz(toc)}";

    /// freedb/CDDB disc ID. Every track counts here, data tracks included, and the
    /// length runs to the disc's real lead-out: this is the one ID that describes
    /// the whole disc rather than its audio.
    ///   n     = sum of the decimal digits of each track's start, in whole seconds
    ///   total = length of the disc in whole seconds
    ///   id    = (n mod 255) << 24 | total << 8 | track count
    public static uint Cddb(Toc toc)
    {
        var n = 0;
        foreach (var t in toc.Tracks) n += DigitSum((t.StartLsn + Toc.LeadIn) / Toc.SectorsPerSecond);

        var total = (toc.LeadoutLsn + Toc.LeadIn) / Toc.SectorsPerSecond
                    - (toc.Tracks[0].StartLsn + Toc.LeadIn) / Toc.SectorsPerSecond;
        return (uint)(n % 255) << 24 | (uint)total << 8 | (uint)toc.Tracks.Count;

        static int DigitSum(int v)
        {
            var sum = 0;
            for (; v > 0; v /= 10) sum += v % 10;
            return sum;
        }
    }
}
