// Deadwax - text size: how large Preferences and the questions are drawn.
//
// COPIED FROM AlbumWall (src/AlbumWall.App/TextSize.cs), 2026-11-01, unchanged
// below this comment: a fix in one goes to the other. Accessibility in
// Preferences (his call, 2026-11-01: "prefs and dialogs only for text size").
// Each window that follows it is drawn through a scale, text, spacing and
// buttons together, and made that much larger to hold it, as a browser's zoom.
//
// THE LIGHT WINDOWS ONLY: Preferences and the confirm questions. Not the main
// window, which is 1444 by 1080 (1100 by 760 at least) and at 150% would
// outgrow most laptop screens.
//
// Applies at once to every window that follows it, open or not.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Deadwax.App;

public static class TextSize
{
    /// The choices, as percentages of the designed size.
    public static readonly int[] Choices = [100, 115, 130, 150];

    public static int Percent { get; private set; } = 100;

    private static event Action? Changed;

    public static void Choose(int? percent)
    {
        var p = percent is { } v && Choices.Contains(v) ? v : 100;
        if (p == Percent) return;
        Percent = p;
        Console.WriteLine($"[text] {Percent}%");
        Changed?.Invoke();
    }

    /// Called at the end of a window's constructor, once its content is in place.
    /// The window is drawn at the chosen size from then on, and follows any
    /// change until it closes. A window that sizes itself to its content needs
    /// nothing more; one with a fixed size is made larger by the same amount,
    /// but never larger than the screen it opens on.
    /// A window that puts new content in later (Properties for the next album, a
    /// statistics page rebuilt) is followed too: whatever it sets is wrapped in
    /// turn.
    public static void Follow(Window window)
    {
        var host = new LayoutTransformControl();
        var content = window.Content as Control;
        double width = window.Width, height = window.Height;
        double minWidth = window.MinWidth, minHeight = window.MinHeight;
        var applied = 1.0;
        var swapping = false;

        void Wrap()
        {
            swapping = true;
            try
            {
                var f = Percent / 100.0;
                if (f == 1)
                {
                    if (window.Content == host) { host.Child = null; window.Content = content; }
                }
                else if (content is not null)
                {
                    host.LayoutTransform = new ScaleTransform(f, f);
                    if (window.Content != host) { window.Content = null; host.Child = content; window.Content = host; }
                }
            }
            finally { swapping = false; }
        }

        window.PropertyChanged += (_, e) =>
        {
            if (swapping || e.Property != ContentControl.ContentProperty || e.NewValue == host) return;
            content = e.NewValue as Control;
            if (Percent != 100) Wrap();
        };

        void Apply()
        {
            var f = Percent / 100.0;
            Wrap();
            if (window.SizeToContent != SizeToContent.Manual) return;
            // A window that can be resized grows from the size it has been
            // dragged to, not from the one it was built with.
            if (window.CanResize && window.IsVisible)
            {
                if (!double.IsNaN(window.Width)) width = window.Width / applied;
                if (!double.IsNaN(window.Height)) height = window.Height / applied;
            }
            applied = f;
            window.MinWidth = minWidth * f;
            window.MinHeight = minHeight * f;
            var w = double.IsNaN(width) ? double.NaN : Math.Ceiling(width * f);
            var h = double.IsNaN(height) ? double.NaN : Math.Ceiling(height * f);
            if ((window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary) is { } screen)
            {
                var area = screen.WorkingArea.Size.ToSize(screen.Scaling);
                if (!double.IsNaN(w)) w = Math.Min(w, area.Width - 48);
                if (!double.IsNaN(h)) h = Math.Min(h, area.Height - 48);
            }
            if (!double.IsNaN(w)) window.Width = w;
            if (!double.IsNaN(h)) window.Height = h;
        }

        Apply();
        Changed += Apply;
        window.Closed += (_, _) => Changed -= Apply;
    }
}
