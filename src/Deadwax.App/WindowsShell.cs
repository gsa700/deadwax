// Who Windows thinks this program is, and where to find it. Ported from
// AlbumWall (2026-10-18), which has the full story; keep the two in step.
//
// An exe in a folder has no identity, and Windows makes one up from the file
// name, so the taskbar cannot pin the running window and a shortcut to it as
// the same thing. The identity is an AppUserModelID, said in two places: by
// the PROCESS at startup, before any window exists, and on the Start Menu
// SHORTCUT as a property of the .lnk file. The shortcut must carry the same
// ID, which WScript.Shell cannot set; it takes IShellLink and IPropertyStore.
//
// Nothing here throws outward. No shortcut is an inconvenience and must never
// be the reason Deadwax does not start.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Deadwax.App;

public static class WindowsShell
{
    /// Permanent. Shortcuts already made carry this string, so changing it
    /// orphans them. The form is Microsoft's: Company.Product — his GitHub
    /// account standing in for the company he does not have.
    public const string AppId = "gsa700.Deadwax";

    private const string Description =
        "Rip CDs into a FLAC library you own";

    /// Called first thing in Main. Harmless anywhere but Windows.
    public static void ClaimIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var hr = SetCurrentProcessExplicitAppUserModelID(AppId);
            if (hr != 0) Console.WriteLine($"[shell] could not set the app id: 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[shell] could not set the app id: {ex.Message}");
        }
    }

    /// %AppData%\Microsoft\Windows\Start Menu\Programs\Deadwax.lnk — per user,
    /// no elevation, and where the Start Menu's own search looks. DoNotVerify, as in
    /// InstallService.ProgramsDirectory: a missing folder must not become "" (the current directory).
    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs, Environment.SpecialFolderOption.DoNotVerify),
        App.DisplayName + ".lnk");

    public static bool HasShortcut => OperatingSystem.IsWindows() && File.Exists(ShortcutPath);

    /// True when the shortcut exists and points at THIS executable. It can exist
    /// and point somewhere else: the app was moved, or built to a new folder —
    /// as it was the day media keys changed the target framework.
    public static bool ShortcutIsCurrent
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(ShortcutPath)) return false;
            try
            {
                return string.Equals(Long(ReadTarget(ShortcutPath)), Long(Environment.ProcessPath ?? ""),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// True when the shortcut points at the INSTALLED copy and that copy exists
    /// — in which case it is the installer's shortcut, not a stray one, and a
    /// development or loose copy has no business re-pointing or removing it. The
    /// installed copy re-asserts it at every start anyway, so taking it would
    /// not even last.
    public static bool ShortcutBelongsToInstalledCopy
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(ShortcutPath)) return false;
            try
            {
                var installed = Install.InstallService.InstalledExePath;
                return File.Exists(installed)
                    && string.Equals(Long(ReadTarget(ShortcutPath)), Long(installed), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// Creates or replaces the Start Menu shortcut. Returns null on success, or
    /// a sentence that can be shown to him as it is.
    public static string? AddShortcut()
    {
        if (!OperatingSystem.IsWindows()) return "Only Windows has a Start Menu.";
        if (Environment.ProcessPath is not { } exe) return "Could not tell where the program is running from.";
        try
        {
            Write(ShortcutPath, exe);
            Console.WriteLine($"[shell] shortcut -> {exe}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[shell] shortcut failed: {ex.Message}");
            return $"Could not create the shortcut: {ex.Message}";
        }
    }

    /// Writes a shortcut at `lnkPath` pointing at `exePath`, carrying the app's
    /// AppUserModelID. For the installer, which points shortcuts at the INSTALLED
    /// copy rather than at whatever is running. Null on success, or a sentence.
    public static string? WriteShortcut(string lnkPath, string exePath)
    {
        if (!OperatingSystem.IsWindows()) return "Only Windows has shortcuts of this kind.";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            Write(lnkPath, exePath);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static string? RemoveShortcut()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not remove the shortcut: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Write(string lnk, string exe)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetDescription(Description);
            link.SetIconLocation(exe, 0);           // the icon compiled into the exe

            // The property that makes it THE shortcut for this app.
            var store = (IPropertyStore)link;
            var key = AppUserModelIdKey;
            var value = new PropVariant { vt = VT_LPWSTR, pointer = Marshal.StringToCoTaskMemUni(AppId) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.pointer);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string ReadTarget(string lnk)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(lnk, 0);
            var buffer = new char[1024];
            link.GetPath(buffer, buffer.Length, IntPtr.Zero, 0);
            return new string(buffer).TrimEnd('\0');
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    // ---- The shell's interfaces, declared only as far as they are used. Method
    // ---- ORDER is the vtable, so the unused ones stay as placeholders.

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, char[] longPath, uint size);

    /// The same file can be named two ways on Windows, a short 8.3 form
    /// (DAVIDE~1) and the long one (David Erickson), and the shell stores
    /// whichever it was given. Compared as text, a shortcut to this very exe then
    /// looked like a shortcut to "a different copy".
    private static string Long(string path)
    {
        var buffer = new char[1024];
        var n = GetLongPathName(path, buffer, (uint)buffer.Length);
        return n > 0 && n < buffer.Length ? new string(buffer, 0, (int)n) : path;
    }

    /// System.AppUserModel.ID
    private static PropertyKey AppUserModelIdKey =>
        new() { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };

    private const ushort VT_LPWSTR = 31;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public IntPtr pointer;
        public IntPtr padding;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }
}
