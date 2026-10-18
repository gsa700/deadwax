using Avalonia;

namespace Deadwax.App;

internal static class Program
{
    /// Set when this run exists only to uninstall: ask, act, and go (InstallFlow).
    public static bool PendingUninstall { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Who this program is to Windows, before any window exists (WindowsShell).
        WindowsShell.ClaimIdentity();

        // --install and --uninstall, the family's switches (AlbumWall's, from
        // LP-100A). Handled here, before Avalonia: an install or a quiet
        // uninstall never opens a window and never touches the drive. The quiet
        // forms are for tools and scripts (winget runs the registered
        // QuietUninstallString, `--uninstall --quiet`). The Uninstall button in
        // Settings > Installed apps runs the plain UninstallString,
        // `--uninstall`, which opens the window only to ask (read back out of
        // the registry by AlbumWall on Hambench, 2026-09-20). Every other
        // argument is ignored, as it always was: this binary has no command
        // line of its own; the deadwax command is Deadwax.Cli.
        var request = Install.InstallCommandLine.Parse(args);
        try
        {
            switch (request.Action)
            {
                case Install.InstallAction.Install:
                    var installed = Install.InstallService.Install();
                    Console.WriteLine($"Installed to {installed.ExePath}");
                    if (!request.Quiet) Install.InstallService.LaunchDetached(installed.ExePath);
                    return installed.Registered ? 0 : 2;

                case Install.InstallAction.Uninstall when request.Quiet:
                    Install.InstallService.Uninstall(new Install.UninstallOptions(RemoveSettings: false));
                    return 0;

                case Install.InstallAction.Uninstall:
                    PendingUninstall = true;
                    break;
            }
        }
        catch (Install.InstallBlockedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
            // macOS: Avalonia's own application-menu items include "About
            // Avalonia"; MainWindow.MacMenu.cs supplies the whole menu instead.
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true });
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
