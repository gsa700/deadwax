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
    /// one of several. SlowSpin false = "Full speed"; 4x is what read Now
    /// and Zen (2026-10-05).
    public bool SlowSpin { get; set; }
    public int SlowSpinSpeed { get; set; } = DefaultSlowSpinSpeed;
    public const int DefaultSlowSpinSpeed = 4;
    /// Open the tray when the rip and post-rip steps are done, as whipper did.
    public bool EjectAfterRip { get; set; }
    /// Where albums go, and whose artist spellings are followed. Null = ~/Music.
    public string? Library { get; set; }
    /// Look for a newer release a few seconds after launch (About tab).
    public bool CheckForUpdates { get; set; } = true;
    /// Run music-unbox, music-backart and music-audit after a clean rip.
    /// Null = not decided: on if the tools are installed.
    public bool? PostRip { get; set; }
    /// For each multi-disc set (keyed by its MusicBrainz release id), whether
    /// its discs are filed as their own albums (true) or kept together
    /// (false). Written when Rip is pressed, so the other discs of the set
    /// offer the same answer. There is no default for a set not listed here.
    public Dictionary<string, bool> SetChoices { get; set; } = new();

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
