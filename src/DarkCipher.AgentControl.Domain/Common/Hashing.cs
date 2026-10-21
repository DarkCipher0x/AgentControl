using System.Security.Cryptography;
using System.Text;

namespace DarkCipher.AgentControl.Domain.Common;

public static class Hashing
{
    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>Dotted version comparison tolerant of prefixes and suffixes ("v2.1.3", "codex-cli 0.161.0", "0.40.6-rc1").</summary>
public static class VersionText
{
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!m.Success) return null;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        try { return new Version(G(1), G(2), G(3), G(4)); } catch (ArgumentOutOfRangeException) { return null; } catch (OverflowException) { return null; }
    }

    /// <summary>-1, 0 or 1; unparseable versions compare as equal (callers treat that as "unknown").</summary>
    public static int Compare(string? a, string? b)
    {
        var (x, y) = (Parse(a), Parse(b));
        return x is null || y is null ? 0 : x.CompareTo(y);
    }

    public static bool AtLeast(string? actual, string? minimum) => minimum is null || (Parse(actual) is { } a && Parse(minimum) is { } m && a >= m);
}
