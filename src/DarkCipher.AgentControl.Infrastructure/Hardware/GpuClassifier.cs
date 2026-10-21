using DarkCipher.AgentControl.Domain.Hardware;

namespace DarkCipher.AgentControl.Infrastructure.Hardware;

/// <summary>Pure classification helpers (unit-tested without WMI).</summary>
public static class GpuClassifier
{
    private static readonly string[] VirtualNameMarkers =
    [
        "parsec", "virtual display", "basic display", "basic render", "remote display", "remotefx", "hyper-v", "meta virtual",
        "citrix", "vmware svga", "virtualbox", "spacedesk", "idd", "displaylink usb", "luminon", "sunshine", "virtual desktop",
    ];

    /// <summary>Virtual adapters are software devices (PnP id under ROOT\) or match a known virtual product name.</summary>
    public static bool IsVirtual(string name, string? pnpDeviceId)
    {
        if (pnpDeviceId is not null && pnpDeviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) return true;
        var n = name.ToLowerInvariant();
        return VirtualNameMarkers.Any(n.Contains);
    }

    public static GpuVendor VendorFrom(string name, string? pnpDeviceId)
    {
        var id = pnpDeviceId?.ToUpperInvariant() ?? "";
        if (id.Contains("VEN_1002", StringComparison.Ordinal)) return GpuVendor.Amd;
        if (id.Contains("VEN_10DE", StringComparison.Ordinal)) return GpuVendor.Nvidia;
        if (id.Contains("VEN_8086", StringComparison.Ordinal)) return GpuVendor.Intel;
        if (id.Contains("VEN_QCOM", StringComparison.Ordinal) || id.Contains("VEN_5143", StringComparison.Ordinal)) return GpuVendor.Qualcomm;
        var n = name.ToLowerInvariant();
        if (n.Contains("radeon") || n.Contains("amd")) return GpuVendor.Amd;
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("quadro")) return GpuVendor.Nvidia;
        if (n.Contains("intel") || n.Contains("arc")) return GpuVendor.Intel;
        if (n.Contains("adreno") || n.Contains("qualcomm")) return GpuVendor.Qualcomm;
        if (n.Contains("microsoft")) return GpuVendor.Microsoft;
        return GpuVendor.Unknown;
    }

    /// <summary>The compute backend Ollama would use for this vendor on Windows. Detection only: whether the specific
    /// GPU is supported is decided by the runtime (reported via its own logs/`ollama ps`), not by this table.</summary>
    public static string? LikelyBackend(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Amd => "ROCm / Vulkan",
        GpuVendor.Nvidia => "CUDA",
        GpuVendor.Intel => "Vulkan (experimental in Ollama)",
        _ => null,
    };

    /// <summary>Parses the registry memory value, which drivers store as QWORD, DWORD or a little-endian byte array.</summary>
    public static long? ParseRegistryMemory(object? value) => value switch
    {
        long l when l > 0 => l,
        int i when i != 0 => (long)(uint)i,
        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
        _ => null,
    };
}
