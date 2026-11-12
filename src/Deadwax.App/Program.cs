using Avalonia;

namespace Deadwax.App;

public static class Program
{
    /// Set when this run exists only to uninstall: ask, act, and go (InstallFlow).
    public static bool PendingUninstall { get; private set; }

    /// Which app this is, for Spindle's install and update code: everything
    /// else (folders, files, repository, bundle id) follows from the name.
    public static AppInfo Identity => new()
    {
        Name = App.DisplayName,
        Description = "Rip CDs into a FLAC library you own",
        DataDir = Path.GetDirectoryName(Settings.FilePath)!,
        AppAssembly = typeof(Program).Assembly,
        DesktopCategories = ["AudioVideo", "Audio", "DiscBurning"],
    };

    [STAThread]
    public static int Main(string[] args)
    {
        AppInfo.Use(Identity);
        // System libraries from the system's places, before anything loads one
        // (Spindle's NativeLoading; Marginal's security review 2026-10-31, C2).
        NativeLoading.Install([.. Identity.NativeLibrariesUnix, .. Identity.NativeLibrariesWindows], typeof(Program).Assembly.GetName().Name!, "Deadwax.Drive");
        WindowSystem.NativeWayland = NativeWayland;     // for Spindle.Ui (WaylandShell, WindowPlacement, DrawnCaptions)

        // The log and the crash catcher first (Diagnostics). No single-instance
        // lock here, unlike AlbumWall: a second copy started while one runs
        // moves the first one's log to deadwax.log.1, where it carries on.
        LogFile.Start(Path.GetDirectoryName(Settings.FilePath)!, "deadwax.log");
        Diagnostics.CatchCrashes(App.DisplayName, InstallService.CurrentVersion,
                                 () => NativeWayland ? "native Wayland" : "X11");
        if (!NativeWayland && ScreenReader.Running)
            Console.WriteLine("[deadwax] backend: X11 through XWayland (a screen reader is running)");

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
        var request = InstallCommandLine.Parse(args);
        try
        {
            switch (request.Action)
            {
                case InstallAction.Install:
                    var installed = InstallService.Install();
                    Console.WriteLine($"Installed to {installed.ExePath}");
                    if (!request.Quiet) InstallService.LaunchDetached(installed.ExePath);
                    return installed.Registered ? 0 : 2;

                case InstallAction.Uninstall when request.Quiet:
                    InstallService.Uninstall(new UninstallOptions(RemoveSettings: false));
                    return 0;

                case InstallAction.Uninstall:
                    PendingUninstall = true;
                    break;
            }
        }
        catch (InstallBlockedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// Native Wayland wherever a Wayland session exists, as AlbumWall does since
    /// 2026-09-21: Fedora 45's mutter stopped giving clicks to borderless X11
    /// windows that are not GTK's, and this window draws its own title bar.
    /// The DEADWAX_X11=1 override was dropped 2026-11-11, with AlbumWall's and
    /// Marginal's GNOME setting: X11 is only the fallback now. Not FreeBSD yet
    /// (Avalonia's Wayland backend fails there at start; see AlbumWall).
    ///
    /// So does a running screen reader (Spindle's ScreenReader, AlbumWall's
    /// check): Avalonia 12.1 speaks to screen readers (AT-SPI) only from its
    /// X11 backend, and on native Wayland Orca could not see Deadwax at all
    /// (Techbench, Fedora 45, 2026-11-03: 0 of 3 starts on the bus, all 3
    /// through XWayland).
    public static readonly bool NativeWayland =
        OperatingSystem.IsLinux()
        && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        && !ScreenReader.Running;

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UseSystemPlatform().WithInterFont()   // Spindle.Ui: UsePlatformDetect, plus FreeBSD
            // macOS: Avalonia's own application-menu items include "About
            // Avalonia"; MainWindow.MacMenu.cs supplies the whole menu instead.
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
            // Avalonia's own warnings (bindings, layout) into the log, not nowhere.
            .AfterSetup(_ => Avalonia.Logging.Logger.Sink = new ConsoleLogSink(Avalonia.Logging.LogEventLevel.Warning));
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
