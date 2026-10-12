using System.Globalization;
using System.Text;

namespace Deadwax.Drive;

/// One CD-Text item as a TOC file holds it: a text pack ("TITLE", "PERFORMER",
/// "ISRC"/"UPC_EAN", "CLOSED", ...) as a string, or a binary pack ("SIZE_INFO",
/// "GENRE", "TOC_INFO1", ...) as its bytes. Key is cdrdao's name for the pack.
public sealed record CdTextItem(string Key, string? Text, IReadOnlyList<byte>? Data)
{
    public static CdTextItem Of(string key, string text) => new(key, text, null);
    public static CdTextItem Of(string key, IReadOnlyList<byte> data) => new(key, null, data);
}

/// One LANGUAGE block of CD-Text: its number and its items in cdrdao's order
/// (by pack type: TITLE, PERFORMER, SONGWRITER, COMPOSER, ARRANGER, MESSAGE,
/// DISC_ID, GENRE, TOC_INFO1, TOC_INFO2, RESERVED1-3, CLOSED, UPC_EAN/ISRC, SIZE_INFO).
public sealed record CdTextBlock(int Language, IReadOnlyList<CdTextItem> Items);

/// The CD-Text of the disc or of one track. Title, Performer, Isrc and UpcEan
/// are the first block's, the four Deadwax uses; Blocks holds everything the
/// disc carries, so the TOC file can be written back exactly as cdrdao wrote
/// it. LanguageMap ("0: 9" = block 0 is English) exists at disc level only.
public sealed record CdText(string? Title, string? Performer, string? Isrc, string? UpcEan,
    IReadOnlyList<CdTextBlock> Blocks, IReadOnlyList<KeyValuePair<int, int>>? LanguageMap = null)
{
    public CdText(string? title, string? performer, string? isrc, string? upcEan)
        : this(title, performer, isrc, upcEan, BlockFrom(title, performer, isrc, upcEan)) { }

    /// A block 0 holding just the named fields, for code that builds CD-Text
    /// by hand (tests, descriptions). ISRC and UPC_EAN share a pack type, so
    /// only one of them is written: the track form or the disc form.
    private static IReadOnlyList<CdTextBlock> BlockFrom(string? title, string? performer, string? isrc, string? upcEan)
    {
        var items = new List<CdTextItem>();
        if (title is not null) items.Add(CdTextItem.Of("TITLE", title));
        if (performer is not null) items.Add(CdTextItem.Of("PERFORMER", performer));
        if (isrc is not null) items.Add(CdTextItem.Of("ISRC", isrc));
        else if (upcEan is not null) items.Add(CdTextItem.Of("UPC_EAN", upcEan));
        return [new CdTextBlock(0, items)];
    }

    /// From the blocks a disc carries: the named fields are the first block's.
    public static CdText FromBlocks(IReadOnlyList<CdTextBlock> blocks, IReadOnlyList<KeyValuePair<int, int>>? languageMap = null)
    {
        var first = blocks.Count > 0 ? blocks[0].Items : [];
        string? Item(string key) => first.FirstOrDefault(i => i.Key == key && i.Text is not null)?.Text;
        return new CdText(Item("TITLE"), Item("PERFORMER"),
            Item("ISRC") is { } isrc ? CdrdaoToc.Isrc(isrc) : null,
            Item("UPC_EAN") is { } upc ? CdrdaoToc.CatalogNumber(upc) : null, blocks, languageMap);
    }
}

public sealed record CdrdaoTrack(
    int Number,
    bool IsAudio,
    string? Isrc,              // from the subchannel
    bool PreEmphasis,
    bool CopyPermitted,
    bool FourChannel,
    int SilenceSectors,        // pregap cdrdao could not or did not read, written as silence
    int PregapSectors,         // index 00 to index 01 ("START")
    int LengthSectors,         // everything the track holds, pregap included
    CdText? Text);

/// A TOC file as `cdrdao read-toc` writes it.
///
/// Where libcdio stops, this starts. On the Pioneer BDR-209D, libcdio's catalog
/// and ISRC calls return nothing for 52nd Street, although the disc has both:
/// the drive does not answer the READ SUB-CHANNEL query they use. cdrdao scans
/// the raw subchannel instead, and reads CD-Text and pregaps in the same pass,
/// which is why whipper used it and why Deadwax keeps it for v1 (spec §4).
public sealed class CdrdaoToc
{
    public string? Catalog { get; private set; }  // from the subchannel (MCN)
    public CdText? DiscText { get; private set; }
    public IReadOnlyList<CdrdaoTrack> Tracks => _tracks;

    private readonly List<CdrdaoTrack> _tracks = [];

    /// The disc's catalog number from either source, subchannel first. A disc
    /// that has none may still report thirteen zeros.
    public string? EffectiveCatalog => Real(Catalog) ?? Real(DiscText?.UpcEan);

    /// The TOC this file describes. A track's index 01 is where everything
    /// before it ends plus its own pregap; the lead-out is where the last ends.
    /// (Track numbers count from 1: every disc in the library starts there.)
    public Toc ToToc()
    {
        if (_tracks.Count == 0) throw new FormatException("TOC file has no tracks.");
        var tracks = new List<TocTrack>(_tracks.Count);
        var at = 0;
        foreach (var t in _tracks)
        {
            tracks.Add(new TocTrack(t.Number, at + t.PregapSectors, t.IsAudio));
            at += t.LengthSectors;
        }
        return new Toc(tracks, at);
    }

    public static string? Real(string? s) => string.IsNullOrWhiteSpace(s) || s.All(c => c == '0') ? null : s;

    /// The codes a disc carries, kept only in their real shape. A CD-Text string
    /// can hold any byte (the TOC writes them as octal escapes), and these are
    /// written unquoted into the .cue and into the tags, so a line break in one
    /// would add lines of the disc's choosing. Security review 2026-10-10.
    /// An ISRC is 12 letters and digits; a catalog number (MCN, UPC/EAN) 12 or 13 digits.
    public static string? Isrc(string s) => s.Length == 12 && s.All(char.IsAsciiLetterOrDigit) ? s : null;

    public static string? CatalogNumber(string s) => s.Length is 12 or 13 && s.All(char.IsAsciiDigit) ? s : null;

    /// A TOC assembled from what the drive said, for the writer.
    public static CdrdaoToc Build(string? catalog, CdText? discText, IEnumerable<CdrdaoTrack> tracks)
    {
        var toc = new CdrdaoToc { Catalog = catalog, DiscText = discText };
        toc._tracks.AddRange(tracks);
        return toc;
    }

    public static CdrdaoToc Parse(string text)
    {
        var toc = new CdrdaoToc();
        var reader = new Reader(text);
        Builder? track = null;

        while (reader.NextToken() is { } token)
        {
            switch (token)
            {
                case "CATALOG":
                    toc.Catalog = CatalogNumber(reader.String());
                    break;
                case "CD_TEXT":
                    var cdText = ReadCdText(reader);
                    if (track is null) toc.DiscText = cdText; else track.Text = cdText;
                    break;
                case "TRACK":
                    if (track is not null) toc._tracks.Add(track.Build());
                    track = new Builder(toc._tracks.Count + 1) { IsAudio = reader.NextToken() == "AUDIO" };
                    break;
                case "ISRC" when track is not null:
                    track.Isrc = Isrc(reader.String());
                    break;
                case "NO":
                    var what = reader.NextToken();
                    if (track is not null && what == "COPY") track.Copy = false;
                    if (track is not null && what == "PRE_EMPHASIS") track.PreEmphasis = false;
                    break;
                case "COPY" when track is not null: track.Copy = true; break;
                case "PRE_EMPHASIS" when track is not null: track.PreEmphasis = true; break;
                case "TWO_CHANNEL_AUDIO" when track is not null: track.FourChannel = false; break;
                case "FOUR_CHANNEL_AUDIO" when track is not null: track.FourChannel = true; break;
                case "SILENCE" or "ZERO" when track is not null:
                    var silence = Msf(reader.NextToken());
                    track.Silence += silence;
                    track.Length += silence;
                    break;
                case "FILE" or "AUDIOFILE" when track is not null:
                    reader.String();                 // "data.wav"
                    Msf(reader.NextToken());         // start in the file
                    track.Length += Msf(reader.NextToken());
                    break;
                case "START" when track is not null:
                    track.Pregap = Msf(reader.NextToken());
                    break;
            }
        }
        if (track is not null) toc._tracks.Add(track.Build());
        return toc;
    }

    /// CD_TEXT { LANGUAGE_MAP { 0: 9 } LANGUAGE 0 { KEY "value" KEY { 1, 2 } ... } }.
    /// Every block and every item is kept, so Write gives the file back.
    private static CdText ReadCdText(Reader reader)
    {
        var blocks = new List<CdTextBlock>();
        List<KeyValuePair<int, int>>? languageMap = null;
        if (reader.NextToken() != "{") throw new FormatException("CD_TEXT without a block.");
        while (reader.NextToken() is { } t && t != "}")
        {
            switch (t)
            {
                case "LANGUAGE_MAP":
                    languageMap = [];
                    if (reader.NextToken() != "{") throw new FormatException("LANGUAGE_MAP without a block.");
                    while (reader.NextToken() is { } m && m != "}")
                    {
                        // "0:" "9", or "0" ":" "9" if ever written with a space before the colon.
                        var slot = m.TrimEnd(':');
                        var code = reader.NextToken();
                        if (code == ":") code = reader.NextToken();
                        languageMap.Add(new(int.Parse(slot, CultureInfo.InvariantCulture), int.Parse(code!, CultureInfo.InvariantCulture)));
                    }
                    break;
                case "LANGUAGE":
                    var number = int.Parse(reader.NextToken()!, CultureInfo.InvariantCulture);
                    if (reader.NextToken() != "{") throw new FormatException("LANGUAGE without a block.");
                    var items = new List<CdTextItem>();
                    while (reader.NextToken() is { } key && key != "}")
                    {
                        var value = reader.NextToken();
                        if (value == "\"") items.Add(CdTextItem.Of(key, reader.LastString!));
                        else if (value == "{")
                        {
                            var data = new List<byte>();
                            while (reader.NextToken() is { } d && d != "}")
                                data.Add(byte.Parse(d.TrimEnd(','), CultureInfo.InvariantCulture));
                            items.Add(CdTextItem.Of(key, data));
                        }
                        else throw new FormatException($"CD-Text item {key} has neither a string nor a block.");
                    }
                    blocks.Add(new CdTextBlock(number, items));
                    break;
            }
        }
        return CdText.FromBlocks(blocks, languageMap);
    }

    // ---- writing ------------------------------------------------------------

    /// The TOC file for this disc, byte for byte as `cdrdao read-toc` 1.2.6
    /// writes it (trackdb/Toc.cc, Track.cc, CdTextContainer.cc, CdTextItem.cc),
    /// so a Deadwax-read disc leaves the same .toc beside its rip as a cdrdao-
    /// read one. Proven by reading every .toc in the library back through
    /// Parse and comparing.
    public string Write()
    {
        var sb = new StringBuilder();
        sb.Append("CD_DA\n\n");
        if (Catalog is not null) sb.Append("CATALOG \"").Append(Catalog).Append("\"\n");
        if (DiscText is not null) WriteCdText(sb, DiscText, isTrack: false);

        var fileStart = 0;
        foreach (var t in _tracks)
        {
            sb.Append("\n// Track ").Append(t.Number.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(t.IsAudio ? "TRACK AUDIO\n" : "TRACK MODE1\n");
            sb.Append(t.CopyPermitted ? "COPY\n" : "NO COPY\n");
            if (t.IsAudio)
            {
                sb.Append(t.PreEmphasis ? "PRE_EMPHASIS\n" : "NO PRE_EMPHASIS\n");
                sb.Append(t.FourChannel ? "FOUR_CHANNEL_AUDIO\n" : "TWO_CHANNEL_AUDIO\n");
                if (t.Isrc is not null) sb.Append("ISRC \"").Append(t.Isrc).Append("\"\n");
                if (t.Text is not null) WriteCdText(sb, t.Text, isTrack: true);
            }
            if (t.SilenceSectors > 0) sb.Append("SILENCE ").Append(Msf(t.SilenceSectors)).Append('\n');
            var fileLength = t.LengthSectors - t.SilenceSectors;
            sb.Append("FILE \"data.wav\" ").Append(fileStart == 0 ? "0" : Msf(fileStart)).Append(' ').Append(Msf(fileLength)).Append('\n');
            fileStart += fileLength;
            if (t.PregapSectors > 0) sb.Append("START ").Append(Msf(t.PregapSectors)).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void WriteCdText(StringBuilder sb, CdText text, bool isTrack)
    {
        if (text.Blocks.Count == 0) return;
        sb.Append("CD_TEXT {\n");
        if (!isTrack && text.LanguageMap is { Count: > 0 } map)
        {
            sb.Append("  LANGUAGE_MAP {\n");
            foreach (var (slot, code) in map)
                sb.Append("    ").Append(slot.ToString(CultureInfo.InvariantCulture)).Append(": ").Append(code.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("  }\n");
        }
        foreach (var block in text.Blocks)
        {
            sb.Append("  LANGUAGE ").Append(block.Language.ToString(CultureInfo.InvariantCulture)).Append(" {\n");
            foreach (var item in block.Items)
            {
                sb.Append("    ").Append(item.Key);
                if (item.Text is { } str)
                {
                    sb.Append(" \"");
                    foreach (var c in str)
                    {
                        if (c == '"') sb.Append("\\\"");
                        else if (c == '\\') sb.Append("\\\\");
                        else sb.Append(c);
                    }
                    sb.Append('"');
                }
                else
                {
                    // "{ 0,  1, 11, ..." : each value %2d, twelve to a line.
                    sb.Append(" {");
                    var i = 0;
                    foreach (var b in item.Data!)
                    {
                        if (i > 0) sb.Append(i % 12 == 0 ? ",\n               " : ", ");
                        sb.Append(b.ToString(CultureInfo.InvariantCulture).PadLeft(2));
                        i++;
                    }
                    sb.Append('}');
                }
                sb.Append('\n');
            }
            sb.Append("  }\n");
        }
        sb.Append("}\n");
    }

    /// "mm:ss:ff" for a sector count, as cdrdao's Msf::str ("%02d:%02d:%02d").
    public static string Msf(int sectors)
    {
        var min = sectors / (Toc.SectorsPerSecond * 60);
        var sec = sectors / Toc.SectorsPerSecond % 60;
        var frames = sectors % Toc.SectorsPerSecond;
        return $"{min:00}:{sec:00}:{frames:00}";
    }

    /// "mm:ss:ff" in sectors, or a bare sample count ("0").
    private static int Msf(string? s)
    {
        if (s is null) throw new FormatException("TOC ends where a time was expected.");
        var parts = s.Split(':');
        if (parts.Length == 1) return (int)(long.Parse(s, CultureInfo.InvariantCulture) / 588);
        return (int.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + int.Parse(parts[1], CultureInfo.InvariantCulture))
               * Toc.SectorsPerSecond + int.Parse(parts[2], CultureInfo.InvariantCulture);
    }

    private sealed class Builder(int number)
    {
        public string? Isrc;
        public bool IsAudio, PreEmphasis, Copy, FourChannel;
        public int Silence, Pregap, Length;
        public CdText? Text;

        public CdrdaoTrack Build() => new(number, IsAudio, Isrc, PreEmphasis, Copy, FourChannel, Silence, Pregap, Length, Text);
    }

    /// Tokens: words, braces, and double-quoted strings with C escapes (cdrdao
    /// writes a stray CR in CD-Text as "\015"; the Skyscraper disc has one).
    /// "//" starts a comment.
    private sealed class Reader(string text)
    {
        private int _at;
        private string? _string;

        /// The text of the quoted string NextToken last returned "\"" for.
        public string? LastString => _string;

        public string? NextToken()
        {
            _string = null;
            while (_at < text.Length)
            {
                var c = text[_at];
                if (char.IsWhiteSpace(c)) { _at++; continue; }
                if (c == '/' && _at + 1 < text.Length && text[_at + 1] == '/')
                {
                    while (_at < text.Length && text[_at] != '\n') _at++;
                    continue;
                }
                if (c is '{' or '}') { _at++; return c.ToString(); }
                if (c == '"') { _string = QuotedString(); return "\""; }
                var start = _at;
                while (_at < text.Length && !char.IsWhiteSpace(text[_at]) && text[_at] is not ('{' or '}' or '"')) _at++;
                return text[start.._at];
            }
            return null;
        }

        public string String()
        {
            if (NextToken() != "\"" || _string is null) throw new FormatException("Expected a quoted string in the TOC.");
            return _string;
        }

        private string QuotedString()
        {
            var sb = new StringBuilder();
            _at++; // opening quote
            while (_at < text.Length && text[_at] != '"')
            {
                var c = text[_at++];
                if (c != '\\' || _at >= text.Length) { sb.Append(c); continue; }
                var e = text[_at];
                if (e is >= '0' and <= '7')
                {
                    var end = _at;
                    while (end < text.Length && end - _at < 3 && text[end] is >= '0' and <= '7') end++;
                    sb.Append((char)Convert.ToInt32(text[_at..end], 8));
                    _at = end;
                }
                else
                {
                    sb.Append(e);
                    _at++;
                }
            }
            _at++; // closing quote
            return sb.ToString();
        }
    }
}
