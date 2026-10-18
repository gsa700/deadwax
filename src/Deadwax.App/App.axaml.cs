using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Deadwax.App;

public sealed class App : Application
{
    /// The product's name wherever a person reads it: dialogs, the Start Menu
    /// shortcut, the Installed-apps entry.
    public const string DisplayName = "Deadwax";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (!OperatingSystem.IsMacOS()) return;
        // The application menu's title on macOS (AlbumWall, 2026-10-14).
        Name = "Deadwax";
        // The application menu must EXIST before Avalonia looks for one, or it
        // makes its own, starting "About Avalonia", and a menu set later does not
        // replace it (AlbumWall, 2026-10-14). Empty here; the main window fills
        // it (MainWindow.MacMenu.cs).
        Avalonia.Controls.NativeMenu.SetMenu(this, new Avalonia.Controls.NativeMenu());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
