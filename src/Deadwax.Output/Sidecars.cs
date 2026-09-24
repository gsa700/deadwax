using System.Text;
using Deadwax.Drive;

namespace Deadwax.Output;

/// What the sidecar writers need to know about one track.
public sealed record SidecarTrack(int Number, string FileName, string Title, string? Performer, int Sectors);

/// The .cue and .m3u files beside a rip, in whipper's layout so the library
/// stays uniform, without its quirks (spec §6).
///
/// Layout: each track's file runs from its INDEX 01 to the next track's INDEX
/// 01, so a track's pregap (INDEX 00) sits at the end of the PREVIOUS file.
/// That is the "gaps appended" style whipper used, and it is what the TOC
/// lengths and every FLAC in the library already assume.
///
/// Differences from whipper, on purpose:
/// - one ISRC line per track (whipper wrote the subchannel's and CD-Text's);
/// - one catalog line, as CATALOG, never an indented UPC_EAN or thirteen zeros;
/// - the gap before track 1 is always a PREGAP line (whipper wrote PREGAP on 32
///   discs and nothing on 5 others with the same kind of gap);
/// - titles and performers are the tags' (MusicBrainz, library spelling), not
///   CD-Text's "Big Shot [Remastered]", and the file says so.
public static class Sidecars
{
    public static string Cue(CdrdaoToc disc, uint cddbId, string albumArtist, string album,
                             IReadOnlyList<SidecarTrack> tracks, string version)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append('\n');

        Line($"REM DISCID {cddbId:X8}");
        Line($"REM COMMENT \"Deadwax {version}\"");
        Line("REM TITLES \"MusicBrainz\"");
        if (disc.EffectiveCatalog is { } catalog) Line($"CATALOG {catalog}");
        Line($"PERFORMER {Quote(albumArtist)}");
        Line($"TITLE {Quote(album)}");

        for (var i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            var d = disc.Tracks.First(x => x.Number == t.Number);
            var gapInPrevious = i > 0 && d.PregapSectors > 0;

            if (!gapInPrevious) Line($"FILE {Quote(t.FileName)} WAVE");
            Line($"  TRACK {t.Number:D2} AUDIO");
            if (d.PreEmphasis) Line("    FLAGS PRE");
            Line($"    TITLE {Quote(t.Title)}");
            if (t.Performer is not null && t.Performer != albumArtist) Line($"    PERFORMER {Quote(t.Performer)}");
            if ((d.Isrc ?? d.Text?.Isrc) is { } isrc) Line($"    ISRC {isrc}");

            if (i == 0 && d.PregapSectors > 0)
            {
                Line($"    PREGAP {Msf(d.PregapSectors)}");
            }
            else if (gapInPrevious)
            {
                Line($"    INDEX 00 {Msf(tracks[i - 1].Sectors - d.PregapSectors)}");
                Line($"FILE {Quote(t.FileName)} WAVE");
            }
            Line("    INDEX 01 00:00:00");
        }
        return sb.ToString();
    }

    /// whipper's playlist exactly: the file name after the comma, and the length
    /// in whole seconds, rounded down.
    public static string M3u(IReadOnlyList<SidecarTrack> tracks)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var t in tracks)
        {
            var seconds = (long)t.Sectors * 588 / 44100;
            sb.Append($"#EXTINF:{seconds},{t.FileName}\n{t.FileName}\n");
        }
        return sb.ToString();
    }

    public static string Msf(int sectors) =>
        $"{sectors / (60 * Toc.SectorsPerSecond):D2}:{sectors / Toc.SectorsPerSecond % 60:D2}:{sectors % Toc.SectorsPerSecond:D2}";

    /// A cue string has no escapes, so a double quote inside one becomes a
    /// single quote; a stray CR or LF (Skyscraper's CD-Text) becomes nothing.
    private static string Quote(string s) =>
        "\"" + s.Replace('"', '\'').Replace("\r", "").Replace("\n", " ") + "\"";
}
