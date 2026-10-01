using System.Text;

namespace BS3D.Api.AdminWeb;

/// <summary>
/// The owner's terminal as the admin page writes to it (issue #5): every control character but the line break (a
/// <c>\n</c>, or the <c>\r\n</c> a Windows console ends its lines with) comes out as '?', so nothing the page prints,
/// its own lines or a log line quoting what a request carried, can move the cursor, retitle the window or send the
/// terminal a command.
/// </summary>
public sealed class TerminalWriter(TextWriter terminal) : TextWriter
{
    public override Encoding Encoding => terminal.Encoding;

    public override void Write(char value) => terminal.Write(Safe(value));

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void Write(string? value)
    {
        if (value != null) terminal.Write(string.Create(value.Length, value, (safe, text) =>
        {
            // A carriage return on its own would write over the line; one that ends it is half a Windows line break
            for (int i = 0; i < text.Length; i++)
                safe[i] = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? '\r' : Safe(text[i]);
        }));
    }

    // The terminal's own line break, which on Windows holds a '\r'
    public override void WriteLine() => terminal.WriteLine();

    public override void WriteLine(string? value)
    {
        Write(value);
        terminal.WriteLine();
    }

    public override void Flush() => terminal.Flush();

    private static char Safe(char c) => c == '\n' || !char.IsControl(c) ? c : '?';
}
