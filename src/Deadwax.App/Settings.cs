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
    /// The drive speed cap, kept between runs: a vibrating disc is usually
    /// one of several. 4x is what read Now and Zen (2026-10-05); the number
    /// is here for anyone whose drive wants a different one.
    public bool SlowSpin { get; set; }
    public int SlowSpinSpeed { get; set; } = DefaultSlowSpinSpeed;
    public const int DefaultSlowSpinSpeed = 4;

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
