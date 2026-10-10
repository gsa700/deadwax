using System.Text.Json;

namespace Deadwax.Verify;

/// The read offsets Deadwax has measured, one per drive:
/// ~/.config/deadwax/drives.json, a map of "VENDOR MODEL REVISION" (as the
/// drive reports them, single-spaced) to the offset in samples, e.g.
/// {"PIONEER BD-RW BDR-209D 1.10": 667}. The only place an offset comes from
/// (whipper's config was read first until 0.2.10). Each drive is its own entry,
/// so several external drives, or one carried between computers, are each
/// measured once and recalled whenever they are plugged in. The key is the
/// model and firmware, which is what an offset belongs to: two drives of the
/// same model and firmware read alike and share one.
public static class DriveOffsets
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "deadwax", "drives.json");

    public static string Key(string vendor, string model, string revision) =>
        string.Join(' ', $"{vendor} {model} {revision}".Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static int? ReadOffset(string vendor, string model, string revision, string? path = null) =>
        All(path).TryGetValue(Key(vendor, model, revision), out var offset) ? offset : null;

    /// Every drive on file, by key.
    public static SortedDictionary<string, int> All(string? path = null)
    {
        path ??= DefaultPath;
        var map = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path)) return map;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject())
                    if (p.Value.TryGetInt32(out var n)) map[Key(p.Name, "", "")] = n;
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return map;
    }

    /// Records a measured offset, keeping every other drive in the file. Written
    /// beside it and moved over it, so a crash mid-write never loses the list.
    public static void Write(string vendor, string model, string revision, int offset, string? path = null)
    {
        path ??= DefaultPath;
        var map = All(path);
        map[Key(vendor, model, revision)] = offset;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".part", JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(path + ".part", path, overwrite: true);
    }

    /// The first words of Advice: how the window knows this failure is one it can fix.
    public const string NotMeasured = "No read offset has been measured for";

    /// What to tell someone whose drive has no offset on file yet.
    public static string Advice(string vendor, string model, string revision) =>
        $"{NotMeasured} {Key(vendor, model, revision)} yet. It is measured once per drive, from a well-known commercial CD, "
        + "and then remembered: Measure in the window, or `deadwax offset --save` with the CD in.";
}
