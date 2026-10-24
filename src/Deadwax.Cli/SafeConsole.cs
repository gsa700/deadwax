using System.Text;
using Deadwax.Drive;

namespace Deadwax.Cli;

/// Standard output with every control character other than line breaks and
/// tabs shown as a visible escape: a backstop under UntrustedText.Show, so a
/// tag or a disc string printed anywhere cannot send escape sequences to the
/// terminal (review 2026-10-24 M3). Progress lines go to standard error,
/// which keeps its "\r\x1b[K".
internal sealed class SafeConsole(TextWriter inner) : TextWriter
{
    public static void Install() => Console.SetOut(Synchronized(new SafeConsole(Console.Out)));

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        if (value is '\n' or '\r' or '\t' || !UntrustedText.IsUnsafe(value)) inner.Write(value);
        else inner.Write(UntrustedText.Show(value.ToString()));
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        if (!value.Any(c => c is not ('\n' or '\r' or '\t') && UntrustedText.IsUnsafe(c))) { inner.Write(value); return; }
        foreach (var c in value) Write(c);
    }

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    public override void WriteLine(string? value) { Write(value); inner.WriteLine(); }
    public override void Flush() => inner.Flush();
}
