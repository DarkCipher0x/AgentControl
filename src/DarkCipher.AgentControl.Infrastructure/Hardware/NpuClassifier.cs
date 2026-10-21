using System.Text.RegularExpressions;

namespace DarkCipher.AgentControl.Infrastructure.Hardware;

/// <summary>Decides whether a PnP device is a neural processing unit. Word-boundary matching only: plain substring
/// matching on "NPU" also matches "INPUT" devices.</summary>
public static partial class NpuClassifier
{
    [GeneratedRegex(@"\bNPU\b|AI Boost|Hexagon|Neural Processing|Ryzen\s*AI|\bIPU\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool IsNpu(string name, string? pnpClass)
    {
        if (string.Equals(pnpClass, "ComputeAccelerator", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(pnpClass, "HIDClass", StringComparison.OrdinalIgnoreCase) || string.Equals(pnpClass, "MEDIA", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pnpClass, "AudioEndpoint", StringComparison.OrdinalIgnoreCase)) return false;
        return NamePattern().IsMatch(name);
    }
}
