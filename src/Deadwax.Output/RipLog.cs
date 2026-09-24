using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Deadwax.Drive;

namespace Deadwax.Output;

public sealed record LogAccurateRip(bool InDatabase, uint LocalCrc, int Confidence, uint? RemoteCrc);

public sealed record LogTrack(
    int Number, string FileName, double Peak, bool PreEmphasis, double Speed,
    uint TestCrc, uint CopyCrc, int Repairs, int SkippedSectors,
    LogAccurateRip V1, LogAccurateRip V2, string Status);

public sealed record LogDisc(
    string Drive, string Engine, int ReadOffset, string GapDetection, bool IsCdr,
    string AlbumArtist, string Album, uint CddbId, string MusicBrainzId, string MusicBrainzLookupUrl,
    string? ReleaseId, Toc Toc);

/// The rip log: a record of what the drive returned, written only by the rip it
/// describes. It is never regenerated and never written for audio that was not
/// read (spec §6), so a "Copy OK" in any log means a real test and copy agreed.
///
/// The layout and key names follow whipper's where the meaning is the same, so
/// the same reader handles both, and so every log in the library looks alike.
/// Deadwax adds what paranoia reported per track (repairs, skipped sectors).
/// The last line is a SHA-256 of everything before it, as whipper's is: the
/// hash of the text up to, not including, the newline before "SHA-256 hash:".
public static class RipLog
{
    public static string Write(LogDisc disc, IReadOnlyList<LogTrack> tracks, string version, DateTimeOffset created)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        string Hex(uint v) => v.ToString("X8", CultureInfo.InvariantCulture);

        L($"Log created by: Deadwax {version}");
        L($"Log creation date: {created.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        L();
        L("Ripping phase information:");
        L($"  Drive: {disc.Drive}");
        L($"  Extraction engine: {disc.Engine}");
        L("  Defeat audio cache: false");
        L($"  Read offset correction: {disc.ReadOffset}");
        L("  Overread into lead-out: false");
        L($"  Gap detection: {disc.GapDetection}");
        L($"  CD-R detected: {(disc.IsCdr ? "true" : "false")}");
        L();
        L("CD metadata:");
        L("  Release:");
        L($"    Artist: {Yaml(disc.AlbumArtist)}");
        L($"    Title: {Yaml(disc.Album)}");
        L($"  CDDB Disc ID: {disc.CddbId:x8}");
        L($"  MusicBrainz Disc ID: {disc.MusicBrainzId}");
        L($"  MusicBrainz lookup URL: {disc.MusicBrainzLookupUrl}");
        if (disc.ReleaseId is not null) L($"  MusicBrainz Release URL: https://musicbrainz.org/release/{disc.ReleaseId}");
        L();
        L("TOC:");
        foreach (var t in disc.Toc.Tracks)
        {
            var end = disc.Toc.EndLsn(t);
            L($"  {t.Number}:");
            L($"    Start: {Msf(t.StartLsn)}");
            L($"    Length: {Msf(end - t.StartLsn)}");
            L($"    Start sector: {t.StartLsn}");
            L($"    End sector: {end - 1}");
            L();
        }
        L("Tracks:");
        foreach (var t in tracks)
        {
            L($"  {t.Number}:");
            L($"    Filename: {Yaml(t.FileName)}");
            L($"    Peak level: {t.Peak.ToString("0.######", CultureInfo.InvariantCulture)}");
            L($"    Pre-emphasis:{(t.PreEmphasis ? " true" : "")}");
            L($"    Extraction speed: {t.Speed.ToString("0.0", CultureInfo.InvariantCulture)} X");
            L($"    Repairs: {t.Repairs}");
            L($"    Skipped sectors: {t.SkippedSectors}");
            L($"    Test CRC: {Hex(t.TestCrc)}");
            L($"    Copy CRC: {Hex(t.CopyCrc)}");
            foreach (var (label, ar) in new[] { ("v1", t.V1), ("v2", t.V2) })
            {
                L($"    AccurateRip {label}:");
                if (!ar.InDatabase) { L("      Result: Track not present in AccurateRip database"); continue; }
                L(ar.Confidence > 0 ? "      Result: Found, exact match" : "      Result: Found, NO exact match");
                if (ar.Confidence > 0) L($"      Confidence: {ar.Confidence}");
                L($"      Local CRC: {Hex(ar.LocalCrc)}");
                if (ar.RemoteCrc is { } r) L($"      Remote CRC: {Hex(r)}");
            }
            L($"    Status: {t.Status}");
            L();
        }

        var accurate = tracks.Count(t => t.V1.Confidence > 0 || t.V2.Confidence > 0);
        var inDatabase = tracks.Count(t => t.V1.InDatabase || t.V2.InDatabase);
        var healthy = tracks.All(t => t.Status == "Copy OK");
        L("Conclusive status report:");
        L("  AccurateRip summary: " + (
            accurate == tracks.Count ? "All tracks accurately ripped"
            : inDatabase == 0 ? "None of the tracks are present in the AccurateRip database"
            : $"{accurate} of {tracks.Count} tracks accurately ripped"));
        L("  Health status: " + (healthy ? "No errors occurred" : "There were errors"));
        L("  EOF: End of status report");

        var body = sb.ToString();
        return body + $"\nSHA-256 hash: {Hash(body)}\n";
    }

    /// Whether a log's SHA-256 line matches its text: whipper's logs and
    /// Deadwax's are checked the same way.
    public static bool Verify(string log)
    {
        var at = log.LastIndexOf("\nSHA-256 hash:", StringComparison.Ordinal);
        if (at < 0) return false;
        var claimed = log[(at + "\nSHA-256 hash:".Length)..].Trim();
        return string.Equals(Hash(log[..at]), claimed, StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    private static string Msf(int sectors) =>
        $"{sectors / (60 * Toc.SectorsPerSecond):D2}:{sectors / Toc.SectorsPerSecond % 60:D2}:{sectors % Toc.SectorsPerSecond:D2}";

    /// Quote a value the way YAML (and whipper) needs when it could otherwise
    /// be misread: a colon-space, a leading quote or symbol, or an all-digit
    /// hex string such as the CRC '40010337'.
    private static string Yaml(string s)
    {
        var needs = s.Contains(": ") || s.Contains(" #") || s.Length == 0
                    || "'\"&*!|>%@`{}[],?-#".Contains(s[0]) || s.All(char.IsDigit);
        return needs ? "'" + s.Replace("'", "''") + "'" : s;
    }
}
