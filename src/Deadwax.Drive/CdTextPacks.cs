using System.Text;

namespace Deadwax.Drive;

/// CD-Text as the drive returns it (READ TOC/PMA/ATIP format 5): 18-byte
/// packs, each a type, a track number, a sequence number, a block/character
/// byte, twelve data bytes and a CRC. Text packs hold NUL-separated strings
/// that run on across packs, one per track (track 0 is the disc); binary
/// packs (GENRE, TOC_INFO1, TOC_INFO2, SIZE_INFO) hold bytes.
///
/// Decoded exactly as cdrdao's processPacks and readCdTextData do it,
/// quirks included, so a Deadwax-read disc gives the same CD_TEXT blocks in
/// its .toc as cdrdao gave whipper.
public static class CdTextPacks
{
    public const int PackBytes = 18;

    /// cdrdao's packType2String: the names a TOC file uses.
    public static string KeyFor(int type, bool isTrack) => type switch
    {
        0x80 => "TITLE", 0x81 => "PERFORMER", 0x82 => "SONGWRITER", 0x83 => "COMPOSER",
        0x84 => "ARRANGER", 0x85 => "MESSAGE", 0x86 => "DISC_ID", 0x87 => "GENRE",
        0x88 => "TOC_INFO1", 0x89 => "TOC_INFO2", 0x8A => "RESERVED1", 0x8B => "RESERVED2",
        0x8C => "RESERVED3", 0x8D => "CLOSED", 0x8E => isTrack ? "ISRC" : "UPC_EAN", 0x8F => "SIZE_INFO",
        _ => "UNKNOWN",
    };

    public static bool IsBinary(int type) => type is 0x87 or 0x88 or 0x89 or 0x8F;

    /// The packs out of a READ TOC/PMA/ATIP format 5 response: a two-byte
    /// length (of what follows it), two reserved bytes, then whole packs.
    /// Null when there are none.
    public static byte[]? FromResponse(byte[] response)
    {
        if (response.Length < 4) return null;
        var available = Math.Min((response[0] << 8 | response[1]) - 2, response.Length - 4);
        var whole = available / PackBytes * PackBytes;
        return whole <= 0 ? null : response.AsSpan(4, whole).ToArray();
    }

    /// One decoded item before it is filed: cdrdao's CdTextItem.
    public sealed record Item(int Type, int Block, int Track, byte[]? TextBytes, byte[]? Data);

    /// The disc's CD-Text and each track's, from the packs (the response
    /// without its four-byte header). Null disc text when there is none.
    public static (CdText? Disc, IReadOnlyDictionary<int, CdText> Tracks) Decode(ReadOnlySpan<byte> packs)
    {
        var items = Items(packs);
        if (items.Count == 0) return (null, new Dictionary<int, CdText>());

        // readCdTextData: the language map and each block's encoding from SIZE_INFO.
        List<KeyValuePair<int, int>>? languageMap = null;
        var encodings = new Encoding[9];
        Array.Fill(encodings, Encoding.Latin1);
        foreach (var item in items)
        {
            if (item.Type != 0x8F || item.Data is null) continue;
            if (languageMap is null && item.Data.Length >= 36)
            {
                languageMap = [];
                for (var i = 0; i < 8; i++)
                    if (item.Data[28 + i] > 0) languageMap.Add(new(i, item.Data[28 + i]));
            }
            if (item.Data.Length >= 1 && item.Block is >= 0 and <= 8)
                encodings[item.Block] = EncodingFor(item.Data[0]);
        }

        // addCdTextItem files by track; CdTextContainer sorts by block, then pack type.
        var byTrack = new Dictionary<int, List<Item>>();
        foreach (var item in items)
        {
            if (!byTrack.TryGetValue(item.Track, out var list)) byTrack[item.Track] = list = [];
            list.Add(item);
        }

        CdText Build(int track)
        {
            var sorted = byTrack[track].OrderBy(i => i.Block).ThenBy(i => i.Type).ToList();
            var blocks = sorted.GroupBy(i => i.Block).Select(g => new CdTextBlock(g.Key,
                g.Select(i => i.TextBytes is { } t
                    ? CdTextItem.Of(KeyFor(i.Type, track > 0), encodings[Math.Min(i.Block, 8)].GetString(t))
                    : CdTextItem.Of(KeyFor(i.Type, track > 0), i.Data!)).ToList())).ToList();
            return CdText.FromBlocks(blocks, track == 0 ? languageMap : null);
        }

        var disc = byTrack.ContainsKey(0) ? Build(0) : null;
        var tracks = byTrack.Keys.Where(t => t > 0).ToDictionary(t => t, Build);
        return (disc, tracks);
    }

    /// cdrdao's processPacks, line for line: strings run on across packs and
    /// end at a NUL, after which the track number steps and padding zeros are
    /// skipped; a pack that starts with a NUL yields an empty string; binary
    /// packs concatenate until the type or block changes; GENRE loses the
    /// zeros after its text; SIZE_INFO belongs to no track.
    public static List<Item> Items(ReadOnlySpan<byte> packs)
    {
        var items = new List<Item>();
        var count = packs.Length / PackBytes;
        if (count == 0) return items;

        var buf = new byte[256 * 12 + 1];
        var pos = 0;
        int lastType = packs[0], lastBlock = (packs[3] >> 4) & 0x07, lastTrack = packs[1];

        void Finish()
        {
            if (!IsBinary(lastType)) return;
            if (lastType == 0x87)
            {
                var i = 2;
                while (i < pos && buf[i] != 0) i++;
                if (i < pos) pos = i + 1;
            }
            items.Add(new Item(lastType, lastBlock, lastType == 0x8F ? 0 : lastTrack, null, buf[..pos]));
        }

        for (var n = 0; n < count; n++)
        {
            var p = packs.Slice(n * PackBytes, PackBytes);
            int type = p[0], track = p[1], block = (p[3] >> 4) & 0x07;
            var data = p.Slice(4, 12);
            var remaining = count - n - 1;

            if (lastType != type || lastBlock != block)
            {
                if (lastType is < 0x80 or > 0x8F) return items;
                Finish();
                lastType = type; lastBlock = block; lastTrack = track;
                pos = 0;
            }

            if (type is < 0x80 or > 0x8F) return items;

            if (IsBinary(type))
            {
                data.CopyTo(buf.AsSpan(pos));
                pos += 12;
                continue;
            }

            var i = 0;
            while (i < 12)
            {
                for (; i < 12 && data[i] != 0; i++) buf[pos++] = data[i];
                if (i >= 12) break;

                // Double-byte text ending in a single zero, with the next pack
                // starting with zero: that zero is the terminator.
                if (i == 11 && (p[3] & 0x80) != 0 && remaining > 0 && packs[(n + 1) * PackBytes + 4] == 0)
                {
                    i = 12;
                    continue;
                }

                items.Add(new Item(type, block, track, buf[..pos], null));
                pos = 0;
                track++;
                while (i < 12 && data[i] == 0) i++;
            }
        }

        if (pos > 0 && lastType is >= 0x80 and <= 0x8F) Finish();
        return items;
    }

    /// Util::characterCodeToEncoding. Only single-byte codes are decoded here;
    /// 0x80-0x82 (Shift-JIS, Korean, Mandarin) fall back to Latin-1 for now.
    private static Encoding EncodingFor(byte code) => code switch
    {
        0x01 => Encoding.ASCII,
        _ => Encoding.Latin1,
    };

    /// CRC-16/CCITT over a pack's first sixteen bytes, stored inverted; for
    /// reporting, since cdrdao does not reject packs on it.
    public static bool CrcOk(ReadOnlySpan<byte> pack) =>
        pack.Length >= PackBytes && (ushort)~QFrame.Crc16(pack[..16]) == (ushort)(pack[16] << 8 | pack[17]);

    // ---- encoding, for tests --------------------------------------------------

    /// Packs for one pack type across tracks 0..N: the strings NUL-separated
    /// and split into twelve-byte packs, as a disc carries them.
    public static byte[] EncodeText(int type, int block, IReadOnlyList<string> textsByTrack, Encoding? encoding = null)
    {
        encoding ??= Encoding.Latin1;
        var stream = new List<byte>();
        foreach (var s in textsByTrack) { stream.AddRange(encoding.GetBytes(s)); stream.Add(0); }
        return Packs(type, block, stream, firstTrack: 0, textsByTrack);
    }

    public static byte[] EncodeBinary(int type, int block, byte[] data)
    {
        var padded = data.ToList();
        while (padded.Count % 12 != 0) padded.Add(0);
        return Packs(type, block, padded, 0, null);
    }

    private static byte[] Packs(int type, int block, List<byte> stream, int firstTrack, IReadOnlyList<string>? texts)
    {
        while (stream.Count % 12 != 0) stream.Add(0);
        var result = new List<byte>();
        var track = firstTrack;
        var seq = 0;
        var charPos = 0;
        for (var at = 0; at < stream.Count; at += 12)
        {
            var pack = new byte[PackBytes];
            pack[0] = (byte)type;
            pack[1] = (byte)track;
            pack[2] = (byte)seq++;
            pack[3] = (byte)((block << 4) | Math.Min(charPos, 15));
            stream.CopyTo(at, pack, 4, 12);
            var crc = (ushort)~QFrame.Crc16(pack.AsSpan(0, 16));
            pack[16] = (byte)(crc >> 8); pack[17] = (byte)crc;
            result.AddRange(pack);
            // The next pack's track is the track of the string in progress at its start.
            var nuls = stream.Skip(at).Take(12).Count(b => b == 0);
            if (texts is not null) { track += nuls; charPos = 0; }
        }
        return result.ToArray();
    }
}
