namespace Deadwax.Output;

/// How a rip is laid out in the library: Preferences > Filing (2026-10-08).
/// The defaults are his library's conventions, so nothing changes for him; a
/// new user can pick another folder or file name style, embed the cover, and
/// leave out the back cover or the playlists.
///
/// Choices from short lists rather than free templates: every style here is
/// one Unbox and the "Filed correctly" check understand, and none can produce
/// an illegal or colliding name. Changes apply to new rips only; Deadwax never
/// renames what is already in the library.
public enum FolderStyle
{
    ArtistYearAlbum,    // Artist/1989 - Album        (his)
    ArtistAlbumYear,    // Artist/Album (1989)
    ArtistAlbum,        // Artist/Album
    ArtistDashAlbum,    // Artist - Album             (one level)
}

public enum TrackStyle
{
    ArtistNumberTitle,  // Artist - 01 - Title.flac   (his)
    NumberDashTitle,    // 01 - Title.flac
    NumberTitle,        // 01 Title.flac
}

public sealed record Filing(
    FolderStyle Folder = FolderStyle.ArtistYearAlbum,
    TrackStyle Track = TrackStyle.ArtistNumberTitle,
    bool EmbedCover = false,
    bool BackCover = true,
    bool Cue = true,
    bool M3u = true)
{
    public static readonly Filing Default = new();

    /// What new rips use. The window sets it from its settings; the command
    /// line keeps the default.
    public static Filing Current { get; set; } = Default;
}
