namespace Deadwax.Metadata;

/// Reading and changing the Vorbis comments of a FLAC already in the library,
/// as FlacWriter writes them, through Deadwax's own FlacMetadata (spec §11
/// step 1; TagLib# did it until 0.5.1). Never touches the audio.
public static class FlacTags
{
    /// Every field, keys upper-cased, grouped by key in the order each key
    /// first appears; a repeated key (PERFORMER) appears once per value.
    public static IReadOnlyList<KeyValuePair<string, string>> Read(string path) => FlacMetadata.Read(path).Fields;

    /// The first value of one field, or null.
    public static string? First(string path, string key) =>
        Read(path).FirstOrDefault(f => f.Key == key.ToUpperInvariant()).Value;

    /// Sets each field in set to its one value (replacing what was there) and
    /// removes each field in remove. Other fields are left as they are.
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

    /// Replaces every Vorbis comment with these (FlacWriter.WriteTags).
    /// Repeated keys become one field with several values, which is how FLAC
    /// stores them anyway (one line each). Any ID3 or APE tag goes.
    public static void Replace(string path, IReadOnlyList<Tag> tags)
    {
        var flac = FlacMetadata.Read(path);
        flac.Clear();
        foreach (var group in tags.GroupBy(t => t.Key))
            flac.Set(group.Key, group.Select(t => t.Value));
        flac.Save(path, dropOtherTags: true);
    }
}
