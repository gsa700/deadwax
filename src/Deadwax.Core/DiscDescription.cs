using System.Text.Json;

namespace Deadwax.Core;

/// What he typed for a disc MusicBrainz does not have: enough for the folder,
/// the tags and the sidecars, nothing more. Kept under the disc ID in
/// ~/.config/deadwax/discs/ so the disc comes back described.
public sealed record DiscDescription(string Artist, string Album, string Year, IReadOnlyList<string> Titles)
{
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "deadwax", "discs");

    private static string PathFor(string discId) => Path.Combine(Directory, discId.Replace('/', '_') + ".json");

    public static DiscDescription? Load(string discId)
    {
        try
        {
            var p = PathFor(discId);
            return File.Exists(p) ? JsonSerializer.Deserialize<DiscDescription>(File.ReadAllText(p)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public void Save(string discId)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(PathFor(discId), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// What is wrong with it, or null.
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Artist)) return "The artist is empty.";
        if (string.IsNullOrWhiteSpace(Album)) return "The album is empty.";
        if (Year.Length != 4 || !Year.All(char.IsAsciiDigit)) return "The year must be four digits.";
        var blank = Titles.Select((t, i) => (t, i)).FirstOrDefault(x => string.IsNullOrWhiteSpace(x.t));
        return blank.t is null && Titles.Count > 0 ? null : $"Track {blank.i + 1} has no title.";
    }
}
