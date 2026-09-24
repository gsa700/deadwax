using System.Buffers.Binary;
using System.Net;
using Deadwax.Drive;

namespace Deadwax.Verify;

/// The three numbers that name a disc's file in the AccurateRip database.
public sealed record AccurateRipId(int AudioTracks, uint Id1, uint Id2, uint Cddb)
{
    /// id1 = sum of the audio tracks' start sectors, plus the audio lead-out.
    /// id2 = sum of start x track number (a start of 0 counts as 1), plus the
    ///       lead-out x (tracks + 1).
    /// Neither adds the 150-sector lead-in. The CDDB part is the ordinary freedb
    /// disc ID, passed in so this class does not depend on Metadata.
    public static AccurateRipId From(Toc toc, uint cddb)
    {
        var tracks = toc.IdTracks.Where(t => t.IsAudio).ToList();
        uint id1 = 0, id2 = 0;
        foreach (var t in tracks)
        {
            id1 += (uint)t.StartLsn;
            id2 += (uint)Math.Max(t.StartLsn, 1) * (uint)t.Number;
        }
        var leadout = (uint)toc.AudioLeadoutLsn;
        id1 += leadout;
        id2 += leadout * (uint)(tracks.Count + 1);
        return new AccurateRipId(tracks.Count, id1, id2, cddb);
    }

    public string FileName => $"dBAR-{AudioTracks:D3}-{Id1:x8}-{Id2:x8}-{Cddb:x8}.bin";

    /// The database shards on the last three hex digits of id1, last digit first.
    public string Url
    {
        get
        {
            var hex = Id1.ToString("x8");
            return $"http://www.accuraterip.com/accuraterip/{hex[7]}/{hex[6]}/{hex[5]}/{FileName}";
        }
    }
}

/// One submitted pressing's checksums for one track. AccurateRip stores v1 and
/// v2 CRCs in the same slots, one per submission, so a slot's CRC can be either.
public sealed record AccurateRipEntry(int Confidence, uint Crc, uint Frame450Crc);

/// One block of the database file: a set of submissions agreeing on a disc.
public sealed record AccurateRipBlock(int TrackCount, uint Id1, uint Id2, uint Cddb, IReadOnlyList<AccurateRipEntry> Tracks);

public static class AccurateRipDatabase
{
    private const int HeaderSize = 13;
    private const int EntrySize = 9;

    /// The .bin is a run of blocks, each a 13-byte header (track count, id1,
    /// id2, cddb) and then 9 bytes per track (confidence, CRC, frame-450 CRC),
    /// little-endian throughout.
    public static IReadOnlyList<AccurateRipBlock> Parse(ReadOnlySpan<byte> data)
    {
        var blocks = new List<AccurateRipBlock>();
        var at = 0;
        while (at < data.Length)
        {
            if (data.Length - at < HeaderSize) throw new FormatException("AccurateRip file ends inside a block header.");
            int count = data[at];
            var id1 = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 1)..]);
            var id2 = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 5)..]);
            var cddb = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 9)..]);
            at += HeaderSize;

            if (data.Length - at < count * EntrySize) throw new FormatException("AccurateRip file ends inside a block.");
            var entries = new AccurateRipEntry[count];
            for (var i = 0; i < count; i++, at += EntrySize)
                entries[i] = new AccurateRipEntry(
                    data[at],
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 1)..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 5)..]));
            blocks.Add(new AccurateRipBlock(count, id1, id2, cddb, entries));
        }
        return blocks;
    }

    /// Fetches a disc's entry. Null when the disc is not in the database, which
    /// is a normal answer and not an error: plenty of pressings have never been
    /// submitted.
    public static async Task<IReadOnlyList<AccurateRipBlock>?> FetchAsync(HttpClient http, AccurateRipId id, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(id.Url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return Parse(bytes);
    }
}

/// How one track's checksums compare with the database.
public sealed record AccurateRipVerdict(int Track, uint V1, uint V2, int V1Confidence, int V2Confidence)
{
    /// Rips that agree with this one, counting whichever version matched more.
    public int Confidence => Math.Max(V1Confidence, V2Confidence);
    public bool IsAccurate => Confidence > 0;
}

public static class AccurateRipMatch
{
    /// Every pressing block is checked; a CRC can sit in any of them, and v1 and
    /// v2 submissions share the slots. The confidence is the best single entry
    /// that equals the CRC, which is how whipper reported it.
    public static AccurateRipVerdict For(IReadOnlyList<AccurateRipBlock>? blocks, int trackIndex, int trackNumber, uint v1, uint v2)
    {
        int c1 = 0, c2 = 0;
        foreach (var b in blocks ?? [])
        {
            if (trackIndex >= b.Tracks.Count) continue;
            var e = b.Tracks[trackIndex];
            if (e.Crc == v1) c1 = Math.Max(c1, e.Confidence);
            if (e.Crc == v2) c2 = Math.Max(c2, e.Confidence);
        }
        return new AccurateRipVerdict(trackNumber, v1, v2, c1, c2);
    }
}
