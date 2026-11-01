// Deadwax — the Keys page of Preferences (accessibility, 2026-11-01; AlbumWall's
// and Marginal's page).
//
// THE LISTS MUST AGREE: the keys are handled by the Mac's menu
// (MainWindow.MacMenu.cs) and, off the Mac, by MainWindow.Keys.cs.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Deadwax.App;

public partial class PrefsWindow
{
    private void FillKeys()
    {
        static string K(string key) => MainWindow.Shortcut(key);
        var mac = OperatingSystem.IsMacOS();

        Heading("The main window", first: true);
        Rows(
            (K("R"), "Read the disc again. Not while a rip runs."),
            (K("E"), "Eject the disc, or open the tray. Not while a rip runs."),
            (K(","), "Preferences."),
            (mac ? "F1   or   ⌘?" : "F1", "This page."),
            (mac ? "⌘W" : null, "Close the window in front."),
            (mac ? "⌘M" : null, "Minimize."),
            (mac ? "⌘H" : null, "Hide Deadwax."),
            (mac ? "⌘Q" : null, "Quit."));
        Note("Tab and Shift+Tab move from control to control; Space or Enter presses the one in focus. "
           + "Each release on the Disc screen is a button: Tab to it and press Space to choose it.");

        Heading("Windows and dialogs");
        Rows(
            ("Esc", "Close Preferences; in a question, the answer that changes nothing."),
            ("Enter", "In the release link box, use that release."));
    }

    private void Heading(string text, bool first = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, first ? 0 : 14, 0, 5)
        };
        Avalonia.Automation.AutomationProperties.SetHeadingLevel(block, 2);
        KeysBody.Children.Add(block);
    }

    private void Note(string text) =>
        KeysBody.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13, LineHeight = 20, Classes = { "dim" },
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 3)
        });

    /// One key and what it does; a row whose keys are null is left out.
    private void Rows(params (string? Keys, string Does)[] rows)
    {
        foreach (var (keys, does) in rows)
        {
            if (keys is null) continue;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), MinHeight = 22 };
            row.Children.Add(new TextBlock
            {
                Text = keys, FontSize = 13, Classes = { "mono" }, VerticalAlignment = VerticalAlignment.Top
            });
            var what = new TextBlock { Text = does, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(what, 1);
            row.Children.Add(what);
            Avalonia.Automation.AutomationProperties.SetName(row, $"{keys}: {does}");
            KeysBody.Children.Add(row);
        }
    }
}
