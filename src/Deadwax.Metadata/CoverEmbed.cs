namespace Deadwax.Metadata;

/// The album's cover.jpg, embedded in every FLAC as its front-cover picture:
/// Preferences > Filing > "Embed the front cover in each file". Off in his
/// library, where art lives only in cover.jpg (snapshot churn, size); on for a
/// fresh install, because phones and car stereos show only embedded art.
///
/// The picture is cover.jpg byte for byte (the Cover Art Archive's 500 px
/// front, typically 50-300 KB), so nothing is re-encoded. Only the metadata
/// changes; the audio and its MD5 are untouched. Any other pictures a file
/// carries (a back, a booklet page) are kept.
public static class CoverEmbed
{
    /// Embeds cover.jpg in every FLAC in the folder; returns how many. Zero
    /// when there is no cover.jpg.
    public static int Apply(string albumDir)
    {
        var coverPath = Path.Combine(albumDir, "cover.jpg");
        if (!File.Exists(coverPath)) return 0;
        var bytes = File.ReadAllBytes(coverPath);
        var count = 0;
        foreach (var flac in Directory.EnumerateFiles(albumDir, "*.flac").Order(StringComparer.Ordinal))
        {
            Embed(flac, bytes);
            count++;
        }
        return count;
    }

    /// One file: the front first, then the pictures it had that are not a
    /// front. The new front's size and depth are left 0, as TagLib# left them.
    public static void Embed(string flac, byte[] cover)
    {
        var file = FlacMetadata.Read(flac);
        var others = file.Pictures.Where(p => p.Type != FlacPicture.FrontCover).ToList();
        file.Pictures.Clear();
        file.Pictures.Add(new FlacPicture(FlacPicture.FrontCover, "image/jpeg", "", 0, 0, 0, 0, cover));
        file.Pictures.AddRange(others);
        file.Save(flac);
    }

    /// Whether a FLAC carries a front-cover picture.
    public static bool HasFront(string flac) => FlacMetadata.Read(flac).Pictures.Any(p => p.Type == FlacPicture.FrontCover);
}
