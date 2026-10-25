using System.Globalization;
using System.Text;

namespace Deadwax.Output;

/// File and folder names, in the style chosen in Filing. The default is the
/// library's convention (whipper's templates): "Artist/YEAR - Album/Artist -
/// NN - Title.flac".
public static class FileNames
{
    public static string Track(string albumArtist, int number, string title) =>
        Track(Filing.Current.Track, albumArtist, number, title);

    public static string Track(TrackStyle style, string albumArtist, int number, string title) => style switch
    {
        TrackStyle.NumberDashTitle => Join($"{number:D2} - ", title, ".flac"),
        TrackStyle.NumberTitle => Join($"{number:D2} ", title, ".flac"),
        _ => Join($"{Part(albumArtist)} - {number:D2} - ", title, ".flac"),
    };

    /// The base name of the album's .toc, .cue, .m3u and .log: "Artist - Album".
    public static string Sidecar(string albumArtist, string album) => Join($"{Part(albumArtist)} - ", album, "");

    /// The most a whole file name may be, in UTF-8 bytes, with room under the
    /// 255 every file system Deadwax writes to allows (a ".log" after a
    /// sidecar's base name included). Each name part alone is held to
    /// PartBytes by Safe.
    public const int NameBytes = 240;
    public const int PartBytes = 200;

    /// prefix + the (safe) part + suffix, the part shortened if the whole would
    /// be longer than NameBytes.
    private static string Join(string prefix, string part, string suffix)
    {
        var room = NameBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix) - 4;   // ".log"
        return prefix + Shorten(Part(part), Math.Max(room, 16)) + suffix;
    }

    /// At most maxBytes of UTF-8, cut between whole characters (an accented
    /// letter or an emoji stays whole), without a trailing space or dot.
    /// Security review 2026-10-24, L10: a 270-character work title made a
    /// file name the file system refused after track 1 had been read, and the
    /// half-written album folder then blocked the rip being tried again.
    public static string Shorten(string s, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(s) <= maxBytes) return s;
        var sb = new StringBuilder();
        var bytes = 0;
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            var n = Encoding.UTF8.GetByteCount(element);
            if (bytes + n > maxBytes) break;
            sb.Append(element);
            bytes += n;
        }
        var cut = sb.ToString().TrimEnd(' ', '.');
        return cut.Length == 0 ? "_" : cut;
    }

    /// The album's own folder name, without the artist folder above it.
    public static string AlbumLeaf(FolderStyle style, string albumArtist, string year, string album) => style switch
    {
        FolderStyle.ArtistAlbumYear => $"{Part(album)} ({Safe(year)})",
        FolderStyle.ArtistAlbum => Safe(album),
        FolderStyle.ArtistDashAlbum => $"{Part(albumArtist)} - {Safe(album)}",
        _ => $"{Part(year)} - {Safe(album)}",
    };

    /// Where an album goes in the library.
    public static string AlbumPath(string library, string albumArtist, string year, string album) =>
        AlbumPath(Filing.Current.Folder, library, albumArtist, year, album);

    public static string AlbumPath(FolderStyle style, string library, string albumArtist, string year, string album) =>
        style == FolderStyle.ArtistDashAlbum
            ? Path.Combine(library, AlbumLeaf(style, albumArtist, year, album))
            : Path.Combine(library, ArtistFolder(albumArtist), AlbumLeaf(style, albumArtist, year, album));

    /// The library an album folder is in: one level up for "Artist - Album",
    /// two for the styles with an artist folder.
    public static string LibraryRoot(string albumDir) =>
        Filing.Current.Folder == FolderStyle.ArtistDashAlbum
            ? Path.GetDirectoryName(albumDir)!
            : Path.GetDirectoryName(Path.GetDirectoryName(albumDir)!)!;

    public static string AlbumFolder(string year, string album) => $"{Part(year)} - {Safe(album)}";

    public static string ArtistFolder(string albumArtist) => Safe(albumArtist);

    /// "/" cannot be in a name at all ("AC/DC" is filed as "AC_DC", as whipper
    /// did). A double quote can, but a .cue cannot name such a file: FILE takes
    /// a quoted string with no escapes, and whipper wrote broken lines for
    /// "The Downeaster "Alexa"". Deadwax writes it with single quotes.
    ///
    /// Names come from MusicBrainz, which anyone can edit, so a name is never
    /// allowed to mean a place: "", "." and ".." become "_" (an artist called
    /// ".." would otherwise file the album beside the library, not in it), and
    /// a line break or other control character becomes a space (it would split
    /// the .m3u's lines). Security review 2026-10-10.
    ///
    /// Windows forbids more: backslash is a separator there too, a colon names
    /// a drive or an alternate stream, * ? < > | are never allowed, a name
    /// cannot end in a dot or a space, and CON, PRN, AUX, NUL, COM1-9 and
    /// LPT1-9 are devices whatever follows them. Only on Windows: the library
    /// on Linux and macOS keeps its colons ("Live: Volume 2"), as it always has.
    public static string Safe(string s) => Safe(s, OperatingSystem.IsWindows());

    public static string Safe(string s, bool windows) => Safe(s, windows, endsName: true);

    /// Safe for a part that something follows in the same name ("Artist - ",
    /// a title before ".flac"): Windows forbids a dot or a space only at the
    /// END of a name, so the part keeps its own. "99 Miles From L.A." is
    /// "... L.A..flac" on every platform, as in the library; until 0.7.4 a
    /// Windows rip wrote "... L.A.flac" (Mobilenode, 2026-10-24).
    public static string Part(string s) => Safe(s, OperatingSystem.IsWindows(), endsName: false);

    public static string Part(string s, bool windows) => Safe(s, windows, endsName: false);

    private static string Safe(string s, bool windows, bool endsName)
    {
        var name = new string(s.Replace('/', '_').Replace('"', '\'').Replace("\0", "")
            .Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c).ToArray());
        if (windows)
        {
            name = new string(name.Select(c => c is '\\' or ':' or '*' or '?' or '<' or '>' or '|' ? '_' : c).ToArray());
            if (endsName) name = name.TrimEnd('.', ' ');
            var stem = name.Split('.')[0];
            if (WindowsDevices.Contains(stem)) name = "_" + name;
        }
        name = Shorten(name, PartBytes);
        if (windows && endsName) name = name.TrimEnd('.', ' ');
        return name is "" or "." or ".." ? "_" : name;
    }

    private static readonly HashSet<string> WindowsDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// True when path is strictly inside library: the last check before an
    /// album folder is written or moved, whatever the names in it said.
    /// Windows paths compare without case; its file systems do the same.
    public static bool IsInside(string library, string path)
    {
        var root = Path.GetFullPath(library).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
