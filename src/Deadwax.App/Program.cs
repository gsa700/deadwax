using Avalonia;

namespace Deadwax.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// Native Wayland wherever a Wayland session exists, as AlbumWall does since
    /// 2026-09-21: Fedora 45's mutter stopped giving clicks to borderless X11
    /// windows that are not GTK's, and this window draws its own title bar.
    /// DEADWAX_X11=1 goes back through XWayland.
    public static readonly bool NativeWayland =
        OperatingSystem.IsLinux()
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && Environment.GetEnvironmentVariable("DEADWAX_X11") != "1";

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
        if (!NativeWayland) return builder;
        // This window draws its own title bar (ExtendClientAreaToDecorationsHint), yet
        // Avalonia 12.1's Wayland backend still asks the compositor for a server-side one
        // (zxdg_toplevel_decoration_v1.set_mode(server_side), then destroys the object).
        // GNOME has no server-side decorations so it never showed; COSMIC honours the
        // request and stacks its grey title bar above ours (seen 2026-10-02, and on
        // AlbumWall 2026-09-26). ForceDrawnDecorations makes the backend skip the
        // decoration protocol altogether, so every desktop looks like GNOME does.
        // Marked experimental: if a future Avalonia drops it this stops compiling rather
        // than silently regressing, and COSMIC is the desktop to recheck.
#pragma warning disable AVALONIA_WAYLAND_FORCE_CSD
        return builder.UseWayland().With(new WaylandPlatformOptions { ForceDrawnDecorations = true });
#pragma warning restore AVALONIA_WAYLAND_FORCE_CSD
    }
}
