// Deadwax — the menu bar, on macOS. AlbumWall's MainWindow.MacMenu.cs, for
// what Deadwax does; keep the two alike.
//
// Every Mac app has one: the application menu with About, Preferences and
// Quit, and menus naming what the app can do, each with its key beside it,
// which is how a Mac user finds the keys at all. Asked for on 2026-10-15, the
// first day on the Mac ("the keys, menu, about and so on").
//
// macOS ONLY. Elsewhere there is no menu bar to fill, and on Linux Avalonia
// would export this to a desktop's global menu, which nobody asked for.
//
// The items run the code the buttons do. A menu key reaches the menu before
// the window, so nothing runs twice. Read Again and Eject are refused while a
// rip runs: the Eject button cancels whatever is in progress, and it is only
// on screen when nothing is, but a menu item is always there.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Deadwax.App;

public partial class MainWindow
{
    private const KeyModifiers Cmd = KeyModifiers.Meta;

    private static NativeMenuItem Item(string header, Action run, Key? key = null, KeyModifiers mods = Cmd)
    {
        var item = new NativeMenuItem(header);
        if (key is { } k) item.Gesture = new KeyGesture(k, mods);
        item.Click += (_, _) => run();
        return item;
    }

    private static NativeMenuItem Menu(string header, params NativeMenuItemBase[] items)
    {
        var menu = new NativeMenu();
        foreach (var i in items) menu.Items.Add(i);
        return new NativeMenuItem(header) { Menu = menu };
    }

    private void SetUpMacMenu()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // The application menu, the one named Deadwax, in full: Avalonia's
        // defaults are off (Program.cs) because they begin with "About Avalonia".
        // The menu App.Initialize made, so that Avalonia never made its own.
        var app = NativeMenu.GetMenu(Avalonia.Application.Current!) ?? new NativeMenu();
        app.Items.Clear();
        app.Items.Add(Item("About Deadwax", () => ShowPrefs("About")));
        app.Items.Add(new NativeMenuItemSeparator());
        app.Items.Add(Item("Preferences…", () => ShowPrefs(null), Key.OemComma));
        app.Items.Add(new NativeMenuItemSeparator());
        app.Items.Add(Item("Hide Deadwax", () => MacWindow.AppAction("hide:"), Key.H));
        app.Items.Add(Item("Hide Others", () => MacWindow.AppAction("hideOtherApplications:"), Key.H, Cmd | KeyModifiers.Alt));
        app.Items.Add(Item("Show All", () => MacWindow.AppAction("unhideAllApplications:")));
        app.Items.Add(new NativeMenuItemSeparator());
        app.Items.Add(Item("Quit Deadwax", Close, Key.Q));
        NativeMenu.SetMenu(Avalonia.Application.Current!, app);

        NativeMenu.SetMenu(this, MenuBar());
    }

    /// File, Disc and Window. macOS shows the menus of the window in FRONT, and
    /// Avalonia keeps them per window, so Preferences gets its own copy (the
    /// first Mac, 2026-10-15: with Preferences open the bar showed only
    /// "Deadwax"). One NativeMenu cannot sit on two windows: a new one each time.
    private NativeMenu MenuBar()
    {
        void UnlessRipping(Action run) { if (!_vm.IsRipping) run(); }

        var bar = new NativeMenu();
        // Cmd+W and Cmd+M act on the window in front: with Preferences in front,
        // Cmd+W closes Preferences, not Deadwax.
        Window Front() => _prefs is { IsActive: true } p ? p : this;
        bar.Items.Add(Menu("File",
            Item("Close Window", () => Front().Close(), Key.W)));
        bar.Items.Add(Menu("Disc",
            Item("Read the Disc Again", () => UnlessRipping(() => OnRescan(null, new RoutedEventArgs())), Key.R),
            Item("Eject", () => UnlessRipping(() => OnEject(null, new RoutedEventArgs())), Key.E)));
        bar.Items.Add(Menu("Window",
            Item("Minimize", () => Front().WindowState = WindowState.Minimized, Key.M),
            Item("Zoom", ToggleMaximized)));
        return bar;
    }

    /// Preferences, opened at a tab by its header ("About"), or as it was.
    private void ShowPrefs(string? tab)
    {
        OnPreferences(null, new RoutedEventArgs());
        if (tab is null || _prefs is null) return;
        foreach (var item in _prefs.Tabs.Items)
            if (item is TabItem { Header: string h } t && h == tab) { _prefs.Tabs.SelectedItem = t; break; }
    }
}
