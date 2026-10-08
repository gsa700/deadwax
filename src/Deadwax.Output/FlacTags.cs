namespace Deadwax.Output;

/// Reading and changing the Vorbis comments of a FLAC already in the library,
/// through TagLibSharp as FlacWriter writes them. Never touches the audio.
public static class FlacTags
{
    /// Every field, keys upper-cased, in the file's order; a repeated key
    /// (PERFORMER) appears once per value.
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

    /// The first value of one field, or null.
    public static string? First(string path, string key) =>
        Read(path).FirstOrDefault(f => f.Key == key.ToUpperInvariant()).Value;

    /// Sets each field in set to its one value (replacing what was there) and
    /// removes each field in remove. Other fields are left as they are.
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
}
