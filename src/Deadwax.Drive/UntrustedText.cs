using System.Globalization;
using System.Text;

namespace Deadwax.Drive;

/// Text from outside Deadwax (FLAC tags in the library, CD-Text, the
/// drive's own strings, MusicBrainz) on its way to a console or a log, which
/// people and Claude read. A line break in it could forge a line of output
/// ("G5: PASS"), an escape sequence could drive the terminal, and a bidi
/// control could make it read differently from what it is (review
/// 2026-10-24 M3, L5, L8). Names on disk go through FileNames.Safe instead.
public static class UntrustedText
{
    /// For a console line: every control character, line or paragraph
    /// separator and bidi control shown as a visible escape (\n, \t,
    /// \u{1B}), so the value stays on its line and reads as what it is.
    public static string Show(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.Any(IsUnsafe)) return s ?? "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (!IsUnsafe(c)) sb.Append(c);
            else sb.Append(c switch
            {
                '\n' => @"\n",
                '\r' => @"\r",
                '\t' => @"\t",
                _ => $@"\u{{{((int)c).ToString("X", CultureInfo.InvariantCulture)}}}",
            });
        }
        return sb.ToString();
    }

    /// For one line of a log file: the same characters turned into spaces.
    public static string OneLine(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.Any(IsUnsafe)) return s ?? "";
        return new string(s.Select(c => IsUnsafe(c) ? ' ' : c).ToArray());
    }

    /// C0 and C1 controls (tab, line feed and escape among them), DEL, the
    /// Unicode line and paragraph separators, and the bidi embedding,
    /// override and isolate controls.
    public static bool IsUnsafe(char c) =>
        c < 0x20 || c is >= (char)0x7F and <= (char)0x9F
        || c is (char)0x2028 or (char)0x2029
        || c is >= (char)0x202A and <= (char)0x202E
        || c is >= (char)0x2066 and <= (char)0x2069;
}
