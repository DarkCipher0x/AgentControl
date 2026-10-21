using System.Text;

namespace DarkCipher.AgentControl.Domain.Processes;

/// <summary>Windows command-line rendering with CommandLineToArgvW-compatible quoting. Used for display, and by the
/// executor's no-inheritance launch of background helpers (other launches use ProcessStartInfo.ArgumentList, which quotes
/// the same way). Lives in the domain so plans can show the exact command.</summary>
public static class ProcessPreview
{
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string Render(string exe, IEnumerable<string> args) => string.Join(' ', new[] { Quote(exe) }.Concat(args.Select(Quote)));
}
