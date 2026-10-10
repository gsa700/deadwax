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
        TrackStyle.NumberDashTitle => $"{number:D2} - {Safe(title)}.flac",
        TrackStyle.NumberTitle => $"{number:D2} {Safe(title)}.flac",
        _ => $"{Safe(albumArtist)} - {number:D2} - {Safe(title)}.flac",
    };

    /// The album's own folder name, without the artist folder above it.
    public static string AlbumLeaf(FolderStyle style, string albumArtist, string year, string album) => style switch
    {
        FolderStyle.ArtistAlbumYear => $"{Safe(album)} ({Safe(year)})",
        FolderStyle.ArtistAlbum => Safe(album),
        FolderStyle.ArtistDashAlbum => $"{Safe(albumArtist)} - {Safe(album)}",
        _ => $"{Safe(year)} - {Safe(album)}",
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

    public static string AlbumFolder(string year, string album) => $"{Safe(year)} - {Safe(album)}";

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
    public static string Safe(string s)
    {
        var name = new string(s.Replace('/', '_').Replace('"', '\'').Replace("\0", "")
            .Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c).ToArray());
        return name is "" or "." or ".." ? "_" : name;
    }

    /// True when path is strictly inside library: the last check before an
    /// album folder is written or moved, whatever the names in it said.
    public static bool IsInside(string library, string path)
    {
        var root = Path.GetFullPath(library).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
    }
}
