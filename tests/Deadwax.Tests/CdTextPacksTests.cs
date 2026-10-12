using System.Text;
using Deadwax.Drive;

namespace Deadwax.Tests;

/// The CD-Text pack decoder, against packs built the way a disc carries them.
public class CdTextPacksTests
{
    private static byte[] SizeInfo(int englishBlocks = 1)
    {
        var data = new byte[36];
        data[0] = 0x00;                 // character code: ISO 8859-1
        data[1] = 1; data[2] = 11;      // first and last track
        data[28] = 9;                   // block 0 is English
        return data;
    }

    [Fact]
    public void Titles_performers_isrcs_and_the_language_map_come_through()
    {
        var packs = new List<byte>();
        packs.AddRange(CdTextPacks.EncodeText(0x80, 0, ["Original Album Classics", "Separate Ways (Worlds Apart)", "Send Her My Love"]));
        packs.AddRange(CdTextPacks.EncodeText(0x81, 0, ["Journey", "Journey", "Journey"]));
        packs.AddRange(CdTextPacks.EncodeText(0x8E, 0, ["886919012927", "USSM18300106", "USSM18300107"]));
        packs.AddRange(CdTextPacks.EncodeBinary(0x8F, 0, SizeInfo()));

        var (disc, tracks) = CdTextPacks.Decode(packs.ToArray());

        Assert.NotNull(disc);
        Assert.Equal("Original Album Classics", disc!.Title);
        Assert.Equal("Journey", disc.Performer);
        Assert.Equal("886919012927", disc.UpcEan);
        Assert.Null(disc.Isrc);
        Assert.Equal([new KeyValuePair<int, int>(0, 9)], disc.LanguageMap!);
        Assert.Equal(["TITLE", "PERFORMER", "UPC_EAN", "SIZE_INFO"], disc.Blocks[0].Items.Select(i => i.Key));

        Assert.Equal(2, tracks.Count);
        Assert.Equal("Separate Ways (Worlds Apart)", tracks[1].Title);
        Assert.Equal("USSM18300106", tracks[1].Isrc);
        Assert.Equal("Send Her My Love", tracks[2].Title);
        Assert.Null(tracks[2].LanguageMap);
    }

    [Fact]
    public void The_toc_written_from_packs_reads_like_cdrdaos()
    {
        var packs = new List<byte>();
        packs.AddRange(CdTextPacks.EncodeText(0x80, 0, ["Volume One", "I Get a Thrill"]));
        packs.AddRange(CdTextPacks.EncodeText(0x81, 0, ["The Honeydrippers", ""]));
        packs.AddRange(CdTextPacks.EncodeBinary(0x8F, 0, SizeInfo()));
        var (disc, tracks) = CdTextPacks.Decode(packs.ToArray());

        var toc = CdrdaoToc.Build("0075679022028", disc,
            [new CdrdaoTrack(1, true, "USAT29900101", false, false, false, 33, 33, 12055, tracks[1])]);
        var text = toc.Write();

        Assert.Contains("CATALOG \"0075679022028\"\nCD_TEXT {\n  LANGUAGE_MAP {\n    0: 9\n  }\n  LANGUAGE 0 {\n    TITLE \"Volume One\"\n    PERFORMER \"The Honeydrippers\"\n    SIZE_INFO { 0,  1, 11,", text);
        Assert.Contains("// Track 1\nTRACK AUDIO\nNO COPY\nNO PRE_EMPHASIS\nTWO_CHANNEL_AUDIO\nISRC \"USAT29900101\"\nCD_TEXT {\n  LANGUAGE 0 {\n    TITLE \"I Get a Thrill\"\n  }\n}\nSILENCE 00:00:33\nFILE \"data.wav\" 0 02:40:22\nSTART 00:00:33\n", text);
        Assert.Equal(text, CdrdaoToc.Parse(text).Write());
    }

    /// cdrdao skips the NULs of empty strings inside a pack as padding, and
    /// keeps an empty string only when it begins a pack. The Megadeth 2026
    /// disc (Golden/megadeth-2026.toc) shows the result: PERFORMER "Megadeth"
    /// for the disc, nothing for tracks 1-3, PERFORMER "" for track 4 alone.
    /// Deadwax reproduces that, so its .toc equals cdrdao's.
    [Fact]
    public void Empty_strings_survive_only_where_cdrdao_kept_them()
    {
        var packs = CdTextPacks.EncodeText(0x81, 0, ["Megadeth", "", "", "", "", "", "", "", "", "", "", ""]);
        var (disc, tracks) = CdTextPacks.Decode(packs);
        Assert.Equal("Megadeth", disc!.Performer);
        Assert.Equal([4], tracks.Keys.Order());
        Assert.Equal("", tracks[4].Performer);
    }

    [Fact]
    public void A_string_running_across_packs_is_one_string()
    {
        var longTitle = new string('x', 30) + " end";
        var packs = CdTextPacks.EncodeText(0x80, 0, [longTitle, "next"]);
        var (disc, tracks) = CdTextPacks.Decode(packs);
        Assert.Equal(longTitle, disc!.Title);
        Assert.Equal("next", tracks[1].Title);
    }

    [Fact]
    public void Genre_keeps_its_codes_and_text_but_not_the_padding()
    {
        var genre = new byte[] { 0, 1, (byte)'R', (byte)'o', (byte)'c', (byte)'k', 0, 0, 0, 0, 0, 0 };
        var (disc, _) = CdTextPacks.Decode(CdTextPacks.EncodeBinary(0x87, 0, genre));
        var item = Assert.Single(disc!.Blocks[0].Items);
        Assert.Equal("GENRE", item.Key);
        Assert.Equal(new byte[] { 0, 1, (byte)'R', (byte)'o', (byte)'c', (byte)'k', 0 }, item.Data);
    }

    [Fact]
    public void Latin1_bytes_decode()
    {
        var packs = CdTextPacks.EncodeText(0x80, 0, ["Mötley Crüe"], Encoding.Latin1);
        var (disc, _) = CdTextPacks.Decode(packs);
        Assert.Equal("Mötley Crüe", disc!.Title);
    }

    [Fact]
    public void Pack_crcs_are_checked_the_inverted_way()
    {
        var packs = CdTextPacks.EncodeText(0x80, 0, ["A"]);
        Assert.True(CdTextPacks.CrcOk(packs.AsSpan(0, 18)));
        packs[5] ^= 1;
        Assert.False(CdTextPacks.CrcOk(packs.AsSpan(0, 18)));
    }
}
