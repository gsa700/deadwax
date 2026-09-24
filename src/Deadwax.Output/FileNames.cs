namespace Deadwax.Output;

/// File and folder names in the library's convention (whipper's templates):
/// "Artist/YEAR - Album/Artist - NN - Title.flac".
public static class FileNames
{
    public static string Track(string albumArtist, int number, string title) =>
        $"{Safe(albumArtist)} - {number:D2} - {Safe(title)}.flac";

    public static string AlbumFolder(string year, string album) => $"{year} - {Safe(album)}";

    public static string ArtistFolder(string albumArtist) => Safe(albumArtist);

    /// "/" cannot be in a name at all ("AC/DC" is filed as "AC_DC", as whipper
    /// did). A double quote can, but a .cue cannot name such a file: FILE takes
    /// a quoted string with no escapes, and whipper wrote broken lines for
    /// "The Downeaster "Alexa"". Deadwax writes it with single quotes.
    public static string Safe(string s) => s.Replace('/', '_').Replace('"', '\'').Replace("\0", "");
}
