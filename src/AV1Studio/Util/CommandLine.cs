using System.Text;

namespace AV1Studio.Util;

/// <summary>Builds a Windows command line (shown to the user and passed to CreateProcess). Each argument is
/// quoted individually with the CommandLineToArgvW rules; no shell ever interprets it.</summary>
public static class CommandLine
{
    public static string Format(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder(Quote(exe));
        foreach (var a in args) sb.Append(' ').Append(Quote(a));
        return sb.ToString();
    }

    /// <summary>Quote using the MSVC/CommandLineToArgvW rules (same as .NET's ArgumentList).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"', '&', '|', '<', '>', '^', '(', ')', '\'', ';', ',']) < 0)
            return arg;

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
