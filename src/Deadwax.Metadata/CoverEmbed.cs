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
    /// The largest picture a FLAC can hold: a metadata block is at most
    /// 16 MB (a 24-bit length), and the picture block's own fields (type,
    /// "image/jpeg", an empty description, size, depth, colours, lengths) take
    /// 42 bytes of it.
    public const int MaxBytes = 0xFFFFFF - 42;

    /// Embeds cover.jpg in every FLAC in the folder; returns how many. Zero
    /// when there is no cover.jpg, or when it is too large for a FLAC to hold
    /// (MaxBytes): the archive allows 20 MB, and embedding one over 16 MB
    /// threw part-way through the rip, before its log was written (security
    /// review 2026-10-24, L11). cover.jpg stays beside the files either way,
    /// and the filing check then says the files have no embedded cover.
    public static int Apply(string albumDir)
    {
        var coverPath = Path.Combine(albumDir, "cover.jpg");
        if (!File.Exists(coverPath) || new FileInfo(coverPath).Length > MaxBytes) return 0;
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
