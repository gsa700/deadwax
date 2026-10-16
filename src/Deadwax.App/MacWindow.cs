// Deadwax — the one thing about a macOS window that Avalonia does not offer.
// AlbumWall's MacWindow.cs; keep the two in step.
//
// The green traffic light is FULL SCREEN on macOS unless the window says
// otherwise, and full screen is switched off on the Mac, as in AlbumWall
// (MainWindow.UseMacTitleBar). Avalonia's only switch for it,
// CanMaximize = false, takes zoom with it and leaves the green button dead. The
// window's collection behavior is the system's own answer: FullScreenNone makes
// the green button zoom, which is what Option-click on it does anyway.
//
// Raw Objective-C runtime calls: nothing to
// bundle, and libobjc is on every Mac.

using System.Runtime.InteropServices;

namespace Deadwax.App;

internal static class MacWindow
{
    private const ulong FullScreenPrimary = 1 << 7;
    private const ulong FullScreenAuxiliary = 1 << 8;
    private const ulong FullScreenNone = 1 << 9;

    /// Makes the green button zoom instead of entering full screen. The handle is
    /// Avalonia's platform handle for the window: an NSWindow, or an NSView whose
    /// window is the one meant. Returns false, and changes nothing, when it is
    /// neither.
    public static bool ZoomInsteadOfFullScreen(IntPtr handle, string? descriptor)
    {
        if (!OperatingSystem.IsMacOS() || handle == IntPtr.Zero) return false;
        var window = descriptor == "NSView" ? Send(handle, Sel("window")) : handle;
        if (window == IntPtr.Zero) return false;

        var behavior = (ulong)Send(window, Sel("collectionBehavior"));
        behavior = behavior & ~(FullScreenPrimary | FullScreenAuxiliary) | FullScreenNone;
        SendULong(window, Sel("setCollectionBehavior:"), behavior);
        return ((ulong)Send(window, Sel("collectionBehavior")) & FullScreenNone) != 0;
    }

    /// Sends an NSApplication action with no sender: hide:, hideOtherApplications:,
    /// unhideAllApplications:. For the application menu, whose standard items
    /// Avalonia's defaults would have supplied (MainWindow.MacMenu.cs).
    public static void AppAction(string selector)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
        SendPtr(app, Sel(selector), IntPtr.Zero);
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendPtr(IntPtr receiver, IntPtr selector, IntPtr value);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendULong(IntPtr receiver, IntPtr selector, ulong value);
}
