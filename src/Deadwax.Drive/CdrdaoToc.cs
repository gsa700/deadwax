using System.Globalization;
using System.Text;

namespace Deadwax.Drive;

public sealed record CdText(string? Title, string? Performer, string? Isrc, string? UpcEan);

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

    /// CD_TEXT { LANGUAGE_MAP { ... } LANGUAGE 0 { KEY "value" ... } }. Only
    /// language block 0 is read; every disc in the library has only that one.
    private static CdText ReadCdText(Reader reader)
    {
        string? title = null, performer = null, isrc = null, upc = null;
        var depth = 0;
        var language = -1;
        while (reader.NextToken() is { } t)
        {
            if (t == "{") { depth++; continue; }
            if (t == "}") { if (--depth == 0) break; continue; }
            if (t == "LANGUAGE" && depth == 1) { language = int.Parse(reader.NextToken()!, CultureInfo.InvariantCulture); continue; }
            if (depth != 2 || language != 0) continue;
            switch (t)
            {
                case "TITLE": title = reader.String(); break;
                case "PERFORMER": performer = reader.String(); break;
                case "ISRC": isrc = Isrc(reader.String()); break;
                case "UPC_EAN": upc = CatalogNumber(reader.String()); break;
            }
        }
        return new CdText(title, performer, isrc, upc);
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
