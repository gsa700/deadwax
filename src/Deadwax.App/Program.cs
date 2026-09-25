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
        return NativeWayland ? builder.UseWayland() : builder;
    }
}
