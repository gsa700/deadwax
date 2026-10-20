namespace Deadwax.Metadata;

/// Reading and changing the Vorbis comments of a FLAC already in the library,
/// as FlacWriter writes them. Never touches the audio.
///
/// Deadwax's own FlacMetadata does it since 0.5.1 (spec §11 step 1);
/// DEADWAX_TAGS=taglib has TagLib# do it instead, as before, for comparing
/// the two (`deadwax check-flac-metadata`). TagLib# goes in the release after.
public static class FlacTags
{
    public static bool UseTagLib => Environment.GetEnvironmentVariable("DEADWAX_TAGS") == "taglib";

    /// Every field, keys upper-cased, grouped by key in the order each key
    /// first appears; a repeated key (PERFORMER) appears once per value.
    public static IReadOnlyList<KeyValuePair<string, string>> Read(string path) =>
        UseTagLib ? TagLibFlac.Read(path) : OwnFlac.Read(path);

    /// The first value of one field, or null.
    public static string? First(string path, string key) =>
        Read(path).FirstOrDefault(f => f.Key == key.ToUpperInvariant()).Value;

    /// Sets each field in set to its one value (replacing what was there) and
    /// removes each field in remove. Other fields are left as they are.
    public static void Update(string path, IReadOnlyDictionary<string, string> set, IEnumerable<string> remove)
    {
        if (UseTagLib) TagLibFlac.Update(path, set, remove);
        else OwnFlac.Update(path, set, remove);
    }

    /// Replaces every Vorbis comment with these (FlacWriter.WriteTags).
    /// Repeated keys become one field with several values, which is how FLAC
    /// stores them anyway (one line each). Any ID3 or APE tag goes.
    public static void Replace(string path, IReadOnlyList<Tag> tags)
    {
        if (UseTagLib) TagLibFlac.Replace(path, tags);
        else OwnFlac.Replace(path, tags);
    }
}

/// Deadwax's own way (FlacMetadata), the default since 0.5.1.
public static class OwnFlac
{
    public static IReadOnlyList<KeyValuePair<string, string>> Read(string path) => FlacMetadata.Read(path).Fields;

    public static void Update(string path, IReadOnlyDictionary<string, string> set, IEnumerable<string> remove)
    {
        var flac = FlacMetadata.Read(path);
        foreach (var key in remove) flac.Remove(key);
        foreach (var (key, value) in set)
        {
            flac.Remove(key);
            flac.Set(key, [value]);
        }
        flac.Save(path);
    }

    public static void Replace(string path, IReadOnlyList<Tag> tags)
    {
        var flac = FlacMetadata.Read(path);
        flac.Clear();
        foreach (var group in tags.GroupBy(t => t.Key))
            flac.Set(group.Key, group.Select(t => t.Value));
        flac.Save(path, dropOtherTags: true);
    }

    public static IReadOnlyList<FlacPicture> Pictures(string path) => FlacMetadata.Read(path).Pictures;

    /// The front first, then the pictures the file had that are not a front.
    /// The new front's size and depth are left 0, as TagLib# left them.
    public static void EmbedFront(string path, byte[] cover)
    {
        var file = FlacMetadata.Read(path);
        var others = file.Pictures.Where(p => p.Type != FlacPicture.FrontCover).ToList();
        file.Pictures.Clear();
        file.Pictures.Add(new FlacPicture(FlacPicture.FrontCover, "image/jpeg", "", 0, 0, 0, 0, cover));
        file.Pictures.AddRange(others);
        file.Save(path);
    }
}

/// The TagLib# way, kept for one release beside FlacMetadata so the two can
/// be compared (DEADWAX_TAGS=taglib, `deadwax check-flac-metadata`).
public static class TagLibFlac
{
    public static IReadOnlyList<KeyValuePair<string, string>> Read(string path)
    {
        using var file = TagLib.File.Create(path, "audio/flac", TagLib.ReadStyle.None);
        var fields = new List<KeyValuePair<string, string>>();
        if (file.GetTag(TagLib.TagTypes.Xiph, create: false) is not TagLib.Ogg.XiphComment xiph) return fields;
        foreach (var key in xiph)
            foreach (var value in xiph.GetField(key))
                fields.Add(new(key.ToUpperInvariant(), value));
        return fields;
    }

    public static void Update(string path, IReadOnlyDictionary<string, string> set, IEnumerable<string> remove)
    {
        using var file = TagLib.File.Create(path, "audio/flac", TagLib.ReadStyle.None);
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, create: true);
        foreach (var key in remove) xiph.RemoveField(key);
        foreach (var (key, value) in set)
        {
            xiph.RemoveField(key);
            xiph.SetField(key, value);
        }
        file.Save();
    }

    public static void Replace(string path, IReadOnlyList<Tag> tags)
    {
        using var file = TagLib.File.Create(path, "audio/flac", TagLib.ReadStyle.None);
        var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph, create: true);
        foreach (var key in xiph.ToList()) xiph.RemoveField(key);
        foreach (var group in tags.GroupBy(t => t.Key))
            xiph.SetField(group.Key, group.Select(t => t.Value).ToArray());
        file.RemoveTags(TagLib.TagTypes.Id3v2 | TagLib.TagTypes.Id3v1 | TagLib.TagTypes.Ape);
        file.Save();
    }

    public static IReadOnlyList<FlacPicture> Pictures(string path)
    {
        using var file = TagLib.File.Create(path, "audio/flac", TagLib.ReadStyle.None);
        return file.Tag.Pictures.Select(p => p is TagLib.Flac.Picture f
            ? new FlacPicture((int)f.Type, f.MimeType, f.Description, f.Width, f.Height, f.ColorDepth, f.IndexedColors, f.Data.Data)
            : new FlacPicture((int)p.Type, p.MimeType, p.Description, 0, 0, 0, 0, p.Data.Data)).ToList();
    }

    public static void EmbedFront(string path, byte[] cover)
    {
        using var file = TagLib.File.Create(path, "audio/flac", TagLib.ReadStyle.None);
        var pictures = file.Tag.Pictures.Where(p => p.Type != TagLib.PictureType.FrontCover).ToList();
        pictures.Insert(0, new TagLib.Picture(new TagLib.ByteVector(cover))
        {
            Type = TagLib.PictureType.FrontCover,
            MimeType = "image/jpeg",
            Description = "",
        });
        file.Tag.Pictures = [.. pictures];
        file.Save();
    }
}
