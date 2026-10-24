using System.Buffers.Binary;

namespace Deadwax.Drive;

/// The disc's table of contents from the drive's own answer to READ TOC
/// format 2 (the full TOC, as MMC defines it), which every platform can ask
/// for: on macOS DKIOCCDREADTOC hands it over as the drive sent it. Step 3
/// of the plan to need no native libraries (spec §11): this replaces
/// libcdio's cdio_get_first_track_num, _num_tracks, _track_lsn,
/// _track_format, _preemphasis, _copy_permit and _channels.
///
/// The answer: two bytes of length, the first and last session, then an
/// 11-byte descriptor per entry: session, ADR (high nibble) and CONTROL (low
/// nibble), TNO, POINT, the running time (MIN, SEC, FRAME), a zero, and
/// PMIN, PSEC, PFRAME. With ADR 1, POINT 1-99 is a track starting at PMSF;
/// A0 names the session's first track, A1 its last, A2 is where its lead-out
/// starts. Only those are read; every other entry is skipped.
public static class FullToc
{
    /// The TOC and each track's CONTROL bits (0x01 pre-emphasis, 0x02 copy
    /// permitted, 0x04 data, 0x08 four channels). The lead-out is the last
    /// session's: what libcdio reports for its lead-out track, and what Toc
    /// works its session gap out from. Throws DriveException on an answer
    /// that is not a TOC.
    public static (Toc Toc, IReadOnlyDictionary<int, byte> Control) Parse(ReadOnlySpan<byte> answer)
    {
        if (answer.Length < 4) throw new DriveException("The drive's TOC is empty.");
        var length = BinaryPrimitives.ReadUInt16BigEndian(answer) + 2;   // the length counts what follows it
        if (length > answer.Length) length = answer.Length;
        var descriptors = (length - 4) / 11;

        var starts = new SortedDictionary<int, int>();
        var control = new Dictionary<int, byte>();
        int? leadout = null;
        var lastSession = -1;
        for (var i = 0; i < descriptors; i++)
        {
            var d = answer.Slice(4 + i * 11, 11);
            int session = d[0], adr = d[1] >> 4, ctl = d[1] & 0x0F, point = d[3];
            if (adr != 1) continue;
            var lsn = Lsn(d[8], d[9], d[10]);
            if (point is >= 1 and <= 99)
            {
                if (lsn < 0) throw new DriveException($"The drive's TOC puts track {point} before the start of the disc.");
                starts[point] = lsn;
                control[point] = (byte)ctl;
            }
            else if (point == 0xA2 && session >= lastSession)
            {
                lastSession = session;
                leadout = lsn;
            }
        }
        if (starts.Count == 0) throw new DriveException("The drive's TOC lists no tracks.");
        if (leadout is null) throw new DriveException("The drive's TOC gives no lead-out.");

        var tracks = starts.Select(kv => new TocTrack(kv.Key, kv.Value, (control[kv.Key] & 0x04) == 0)).ToList();
        try { return (new Toc(tracks, leadout.Value), control); }
        catch (ArgumentException e) { throw new DriveException($"The drive's TOC does not hold together: {e.Message}"); }
    }

    /// MSF to a logical sector: the first 2 seconds (150 frames) are the lead-in's.
    private static int Lsn(byte min, byte sec, byte frame) => (min * 60 + sec) * Toc.SectorsPerSecond + frame - Toc.LeadIn;
}
