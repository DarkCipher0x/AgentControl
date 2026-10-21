namespace DarkCipher.AgentControl.Domain.Hardware;

/// <summary>Detection is not support: each fact carries its own state.</summary>
public enum DetectionState
{
    Unknown,
    Detected,
    Supported,
    Configured,
    Available,
    InUse,
    Unsupported,
}

public enum GpuVendor { Unknown, Amd, Nvidia, Intel, Qualcomm, Microsoft, Virtual }

public enum HardwareRating
{
    CompatibilityUnknown,
    ExcellentForLocalAi,
    Good,
    SuitableForSmallerModels,
    CpuOnly,
    MemoryLimited,
    DriverUpdateRecommended,
    RuntimeSupportUnavailable,
}

public sealed record OsInfo(string Caption, string Version, string Build, string Architecture);

public sealed record CpuInfo(string Name, int Cores, int LogicalProcessors, IReadOnlyList<string> InstructionSets);

public sealed record MemoryInfo(long TotalBytes, long AvailableBytes);

/// <summary>A display adapter. <see cref="DedicatedMemoryBytes"/> comes from the driver registry (qwMemorySize) where
/// possible, because Win32_VideoController.AdapterRAM is a 32-bit field that caps at 4 GB.</summary>
public sealed record GpuInfo(
    string Name,
    GpuVendor Vendor,
    long DedicatedMemoryBytes,
    string MemorySource,
    string? DriverVersion,
    DateTimeOffset? DriverDate,
    bool IsVirtual,
    DetectionState State,
    string? Backend);

public sealed record DiskInfo(string Root, string? Label, string Format, long TotalBytes, long FreeBytes, bool IsRemovable, bool IsNetwork);

/// <summary>A neural processing unit. Detection never implies that an inference runtime can use it.</summary>
public sealed record NpuInfo(string Name, string Vendor, DetectionState State, string RuntimeNote);

public sealed record HardwareProfile(
    OsInfo Os,
    CpuInfo Cpu,
    MemoryInfo Memory,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<NpuInfo> Npus,
    DateTimeOffset ScannedAt,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Physical GPUs only (virtual adapters such as Parsec, Basic Display or RDP are excluded).</summary>
    public IEnumerable<GpuInfo> PhysicalGpus => Gpus.Where(g => !g.IsVirtual);

    public GpuInfo? PrimaryGpu => PhysicalGpus.OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault();
}

public sealed record HardwareAssessment(HardwareRating Rating, string Headline, IReadOnlyList<string> Reasons);

/// <summary>Port: platform-specific hardware discovery.</summary>
public interface IHardwareDiscoveryProvider
{
    Task<HardwareProfile> ScanAsync(CancellationToken ct);
}
