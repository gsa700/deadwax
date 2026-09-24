using System.Globalization;
using Deadwax.Drive;

namespace Deadwax.Verify;

public sealed record WhipperArResult(string? Result, int? Confidence, uint? LocalCrc, uint? RemoteCrc)
{
    public bool IsMatch => Result is not null && Result.StartsWith("Found, exact match", StringComparison.Ordinal);
}

public sealed record WhipperLogTrack(int Number, uint? TestCrc, uint? CopyCrc, WhipperArResult? V1, WhipperArResult? V2, string? Status);

/// A whipper 0.10.0 rip log, read for the numbers Deadwax is checked against.
///
/// The log looks like YAML but is not reliably YAML (whipper quotes values only
/// when it has to), so it is read line by line for the few keys that matter.
/// This is a yardstick for Deadwax, not something Deadwax writes.
public sealed class WhipperLog
{
    public string Path { get; }
    public string? Drive { get; private set; }
    public int? ReadOffset { get; private set; }
    public uint? CddbId { get; private set; }
    public string? MusicBrainzId { get; private set; }
    public string? MusicBrainzLookupUrl { get; private set; }

    /// TOC entries as whipper listed them: number, first sector, last sector.
    /// Entry 0, when present, is the stretch before track 1 (see HasPreTrackGap).
    public IReadOnlyList<(int Number, int Start, int End)> TocEntries => _toc;
    public IReadOnlyList<WhipperLogTrack> Tracks => _tracks;

    private readonly List<(int Number, int Start, int End)> _toc = [];
    private readonly List<WhipperLogTrack> _tracks = [];

    private WhipperLog(string path) => Path = path;

    /// whipper lists audio before track 1 as a "track 0" entry in its TOC, even
    /// when it is under half a second and never ripped to a file.
    public bool HasPreTrackGap => _toc.Count > 0 && _toc[0].Number == 0;

    /// The MusicBrainz TOC string from the lookup URL: whipper's own statement
    /// of the TOC, in the form DiscIds.MusicBrainzToc produces.
    public string? MusicBrainzToc => QueryValue("toc");

    /// The disc's TOC rebuilt from the log. whipper logs do not say which
    /// tracks are data, so every track is taken as audio; a log whose track count
    /// disagrees with its MusicBrainz URL is the sign that assumption broke.
    public Toc ToToc()
    {
        var entries = _toc.Where(e => e.Number > 0).ToList();
        if (entries.Count == 0) throw new FormatException($"{Path}: no TOC.");
        return new Toc(entries.Select(e => new TocTrack(e.Number, e.Start, IsAudio: true)).ToList(), entries[^1].End + 1);
    }

    public static WhipperLog Load(string path)
    {
        var log = new WhipperLog(path);
        log.Parse(File.ReadAllLines(path));
        return log;
    }

    private enum Section { None, Toc, Tracks }

    private void Parse(string[] lines)
    {
        var section = Section.None;
        int? number = null;
        int? start = null, end = null;
        uint? test = null, copy = null;
        string? status = null;
        WhipperArResult? v1 = null, v2 = null;
        int arVersion = 0;

        void FlushToc()
        {
            if (number is { } n && start is { } s && end is { } e) _toc.Add((n, s, e));
            start = end = null;
        }

        void FlushTrack()
        {
            if (number is { } n) _tracks.Add(new WhipperLogTrack(n, test, copy, v1, v2, status));
            test = copy = null; status = null; v1 = v2 = null; arVersion = 0;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            if (indent == 0 && trimmed.Length > 0)
            {
                if (section == Section.Toc) FlushToc();
                if (section == Section.Tracks) FlushTrack();
                number = null;
                section = trimmed switch
                {
                    "TOC:" => Section.Toc,
                    "Tracks:" => Section.Tracks,
                    _ => Section.None,
                };
                continue;
            }

            // "  7:" opens a track entry inside TOC: or Tracks:.
            if (section != Section.None && indent == 2 && trimmed.EndsWith(':') &&
                int.TryParse(trimmed[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var entry))
            {
                if (section == Section.Toc) FlushToc(); else FlushTrack();
                number = entry;
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            var key = trimmed[..colon];
            var value = Unquote(trimmed[(colon + 1)..].Trim());

            switch (section)
            {
                case Section.None:
                    switch (key)
                    {
                        case "Drive": Drive = value; break;
                        case "Read offset correction": ReadOffset = int.Parse(value, CultureInfo.InvariantCulture); break;
                        case "CDDB Disc ID": CddbId = Hex(value); break;
                        case "MusicBrainz Disc ID": MusicBrainzId = value; break;
                        case "MusicBrainz lookup URL": MusicBrainzLookupUrl = value; break;
                    }
                    break;

                case Section.Toc:
                    if (key == "Start sector") start = int.Parse(value, CultureInfo.InvariantCulture);
                    else if (key == "End sector") end = int.Parse(value, CultureInfo.InvariantCulture);
                    break;

                case Section.Tracks:
                    switch (key)
                    {
                        case "Test CRC": test = Hex(value); break;
                        case "Copy CRC": copy = Hex(value); break;
                        case "Status": status = value; break;
                        case "AccurateRip v1": arVersion = 1; break;
                        case "AccurateRip v2": arVersion = 2; break;
                        case "Result" or "Confidence" or "Local CRC" or "Remote CRC" when arVersion != 0:
                            var current = (arVersion == 1 ? v1 : v2) ?? new WhipperArResult(null, null, null, null);
                            current = key switch
                            {
                                "Result" => current with { Result = value },
                                "Confidence" => current with { Confidence = int.Parse(value, CultureInfo.InvariantCulture) },
                                "Local CRC" => current with { LocalCrc = Hex(value) },
                                _ => current with { RemoteCrc = Hex(value) },
                            };
                            if (arVersion == 1) v1 = current; else v2 = current;
                            break;
                    }
                    break;
            }
        }

        if (section == Section.Toc) FlushToc();
        if (section == Section.Tracks) FlushTrack();
    }

    private string? QueryValue(string name)
    {
        if (MusicBrainzLookupUrl is null) return null;
        var q = MusicBrainzLookupUrl.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in MusicBrainzLookupUrl[(q + 1)..].Split('&'))
            if (pair.StartsWith(name + "=", StringComparison.Ordinal)) return pair[(name.Length + 1)..];
        return null;
    }

    private static string Unquote(string v) =>
        v.Length >= 2 && (v[0] == '\'' && v[^1] == '\'' || v[0] == '"' && v[^1] == '"') ? v[1..^1] : v;

    private static uint? Hex(string v) =>
        uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var x) ? x : null;
}
