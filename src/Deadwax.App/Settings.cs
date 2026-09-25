using System.Text.Json;

namespace Deadwax.App;

/// What the window remembers between runs, in ~/.config/deadwax/settings.json.
/// Size and maximized only: on native Wayland a window cannot place itself
/// (its position always reads 0,0), so GNOME decides where it opens, as with
/// AlbumWall.
public sealed class Settings
{
    public double Width { get; set; } = 1444;
    public double Height { get; set; } = 1080;
    public bool Maximized { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "deadwax", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
    }
}
