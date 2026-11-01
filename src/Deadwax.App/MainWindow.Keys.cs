// Deadwax — the keyboard off the Mac (accessibility, 2026-11-01).
//
// The Mac's menu bar carries these keys (MainWindow.MacMenu.cs); Linux and
// Windows have no menu bar, so they had none at all. The same keys, with Ctrl,
// run the same code here; on the Mac the menu takes them first and this does
// nothing for them. F1 (and ⌘? on the Mac) opens the Keys page.
//
//   Ctrl+R  read the disc again    Ctrl+E  eject (or open the tray)
//   Ctrl+,  Preferences            F1      the Keys page
//
// Read again and Eject are refused while a rip runs, as in the menu.
// THE LISTS MUST AGREE: every key here is on the Keys page (PrefsWindow.Keys.cs).

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Deadwax.App;

public partial class MainWindow
{
    /// "⌘R" on the Mac, "Ctrl+R" elsewhere.
    internal static string Shortcut(string key) => OperatingSystem.IsMacOS() ? $"⌘{key}" : $"Ctrl+{key}";

    private void SetUpKeys()
    {
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        ToolTip.SetTip(SettingsButton, $"Preferences ({Shortcut(",")})");
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if ((e.Key == Key.F1 && mods == KeyModifiers.None)
            || (OperatingSystem.IsMacOS() && e.Key == Key.OemQuestion && mods == (KeyModifiers.Meta | KeyModifiers.Shift)))
        {
            ShowPrefs("Keys");
            e.Handled = true;
            return;
        }
        if (OperatingSystem.IsMacOS() || mods != KeyModifiers.Control) return;   // the Mac's menu has them
        var args = new RoutedEventArgs();
        switch (e.Key)
        {
            case Key.R when !_vm.IsRipping: OnRescan(null, args); break;
            case Key.E when !_vm.IsRipping: OnEject(null, args); break;
            case Key.OemComma: ShowPrefs(null); break;
            default: return;
        }
        e.Handled = true;
    }
}
