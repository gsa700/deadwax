using System.Text;

namespace Deadwax.App;

/// The Windows helpers' PowerShell: where it is, and how a value goes into a
/// script (review 2026-10-24 M4, L13).
internal static class PowerShell
{
    /// By its full path in System32: started by its bare name, Windows looks
    /// in this program's own folder first, so a powershell.exe dropped beside
    /// a loose copy in Downloads would run instead.
    public static string Exe => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// Started on a script file, with no window, outside this program's folder.
    public static System.Diagnostics.ProcessStartInfo StartInfo(string script) => new(Exe)
    {
        ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetTempPath(),
    };

    /// A single-quoted literal. PowerShell takes the curly single quotes
    /// (U+2018 to U+201B) as quotes too, so a path like "Sean O’Brien" would
    /// end a literal escaped for ' alone; each is doubled, as PowerShell's
    /// own EscapeSingleQuotedStringContent does.
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            if (c is '\'' or (char)0x2018 or (char)0x2019 or (char)0x201A or (char)0x201B) sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// Arguments as one Windows command line (the rules CommandLineToArgvW
    /// reads back), for Start-Process: given a list, it joins the items with
    /// spaces and no quoting, so an argument with a space in it was split.
    public static string CommandLine(IEnumerable<string> args) => string.Join(" ", args.Select(Argument));

    private static string Argument(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"')) return arg;
        var sb = new StringBuilder().Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
