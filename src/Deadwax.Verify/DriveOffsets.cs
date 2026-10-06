using System.Text.Json;

namespace Deadwax.Verify;

/// Deadwax's own read offsets, for a drive whipper never measured:
/// ~/.config/deadwax/drives.json, a map of "VENDOR MODEL REVISION" (as the
/// drive reports them, single-spaced) to the offset in samples, e.g.
/// {"PIONEER BD-RW BDR-209D 1.10": 667}. The number for a drive is in the
/// AccurateRip offset list (accuraterip.com/driveoffsets.htm). whipper.conf is
/// read first (WhipperConfig); this is the fallback, and the file a copy
/// without whipper will use.
public static class DriveOffsets
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "deadwax", "drives.json");

    public static string Key(string vendor, string model, string revision) =>
        string.Join(' ', $"{vendor} {model} {revision}".Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static int? ReadOffset(string vendor, string model, string revision, string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var wanted = Key(vendor, model, revision);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (Key(p.Name, "", "") == wanted && p.Value.TryGetInt32(out var offset)) return offset;
        }
        catch (JsonException) { }
        return null;
    }

    /// Records a measured offset, keeping any other drives in the file.
    public static void Write(string vendor, string model, string revision, int offset, string? path = null)
    {
        path ??= DefaultPath;
        var map = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Value.TryGetInt32(out var n)) map[Key(p.Name, "", "")] = n;
            }
            catch (JsonException) { }
        }
        map[Key(vendor, model, revision)] = offset;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// What to tell someone whose drive has no offset on file.
    public static string Advice(string vendor, string model, string revision) =>
        $"No read offset is known for {Key(vendor, model, revision)}. Put it in {DefaultPath} as " +
        $"{{\"{Key(vendor, model, revision)}\": N}}; the number for your drive is in the AccurateRip list at accuraterip.com/driveoffsets.htm.";
}
