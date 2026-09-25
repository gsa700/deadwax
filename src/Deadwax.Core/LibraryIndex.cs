namespace Deadwax.Core;

/// Which albums in the library came from which disc, by the MusicBrainz disc ID
/// every rip log records (whipper's and Deadwax's alike). Reading 310 logs takes
/// a fraction of a second, so the Disc screen can say "already in your library"
/// from the TOC alone, before the two-minute subchannel read.
public static class LibraryIndex
{
    public static string? FindDisc(string library, string discId)
    {
        if (!Directory.Exists(library)) return null;
        var needle = "MusicBrainz Disc ID: " + discId;
        foreach (var log in Directory.EnumerateFiles(library, "*.log", SearchOption.AllDirectories))
        {
            try
            {
                foreach (var line in File.ReadLines(log))
                    if (line.Trim() == needle) return Path.GetDirectoryName(log);
            }
            catch (IOException) { }
        }
        return null;
    }
}
