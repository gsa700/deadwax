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
        var bytes = new TagLib.ByteVector(File.ReadAllBytes(coverPath));
        var count = 0;
        foreach (var flac in Directory.EnumerateFiles(albumDir, "*.flac").Order(StringComparer.Ordinal))
        {
            using var file = TagLib.File.Create(flac, "audio/flac", TagLib.ReadStyle.None);
            var pictures = file.Tag.Pictures.Where(p => p.Type != TagLib.PictureType.FrontCover).ToList();
            pictures.Insert(0, new TagLib.Picture(bytes)
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
                Description = "",
            });
            file.Tag.Pictures = [.. pictures];
            file.Save();
            count++;
        }
        return count;
    }

    /// Whether a FLAC carries a front-cover picture.
    public static bool HasFront(string flac)
    {
        using var file = TagLib.File.Create(flac, "audio/flac", TagLib.ReadStyle.None);
        return file.Tag.Pictures.Any(p => p.Type == TagLib.PictureType.FrontCover);
    }
}
