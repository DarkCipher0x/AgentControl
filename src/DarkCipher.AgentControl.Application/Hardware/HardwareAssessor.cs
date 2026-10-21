using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;

namespace DarkCipher.AgentControl.Application.Hardware;

/// <summary>Turns a hardware profile into a plain-language rating with reasons ("Why this recommendation?").
/// Thresholds are conservative and explained; detection is never treated as runtime support.</summary>
public static class HardwareAssessor
{
    public static HardwareAssessment Assess(HardwareProfile hw)
    {
        var reasons = new List<string>();
        var ramGb = hw.Memory.TotalBytes / (double)Bytes.GiB;
        var gpu = hw.PrimaryGpu;
        var vramGb = gpu is null ? 0 : gpu.DedicatedMemoryBytes / (double)Bytes.GiB;

        reasons.Add(gpu is null
            ? "No physical GPU was detected, so models would run on the CPU."
            : $"{gpu.Name}: {vramGb:0.#} GiB dedicated memory (source: {gpu.MemorySource}).");
        reasons.Add($"System memory: {ramGb:0.#} GiB total, {hw.Memory.AvailableBytes / (double)Bytes.GiB:0.#} GiB free now.");
        foreach (var v in hw.Gpus.Where(g => g.IsVirtual)) reasons.Add($"Ignored virtual adapter: {v.Name}.");
        foreach (var n in hw.Npus) reasons.Add($"NPU detected ({n.Name}) — {n.RuntimeNote}");

        if (gpu is not null && gpu.MemorySource.Contains("capped", StringComparison.OrdinalIgnoreCase))
            reasons.Add("The GPU memory figure may be capped at 4 GB by Windows; the real amount could be higher.");

        if (gpu is not null && gpu.DriverDate is { } dd && dd < DateTimeOffset.UtcNow.AddYears(-1))
        {
            reasons.Add($"The graphics driver is from {dd:yyyy-MM-dd}; newer drivers usually improve local AI support.");
            return new HardwareAssessment(HardwareRating.DriverUpdateRecommended, "Driver update recommended", reasons);
        }

        if (gpu is not null && gpu.Vendor is GpuVendor.Amd or GpuVendor.Nvidia)
        {
            if (vramGb >= 15 && ramGb >= 31)
            {
                reasons.Add("16 GB-class GPU memory fits ~20B-parameter models at 4-bit with a large context entirely on the GPU.");
                return new HardwareAssessment(HardwareRating.ExcellentForLocalAi, "Excellent for local AI", reasons);
            }
            if (vramGb >= 10)
            {
                reasons.Add("10–15 GB of GPU memory fits 7–14B models with a useful context on the GPU.");
                return new HardwareAssessment(HardwareRating.Good, "Good for local AI", reasons);
            }
            if (vramGb >= 5.5)
            {
                reasons.Add("6–8 GB of GPU memory suits 3–8B models; larger models will spill into slower system memory.");
                return new HardwareAssessment(HardwareRating.SuitableForSmallerModels, "Suitable for smaller models", reasons);
            }
        }
        if (gpu is not null && gpu.Vendor is GpuVendor.Intel or GpuVendor.Qualcomm)
        {
            reasons.Add("Local runtimes have limited support for this GPU vendor on Windows; expect CPU inference.");
            if (ramGb < 15) return new HardwareAssessment(HardwareRating.MemoryLimited, "Memory limited", reasons);
            return new HardwareAssessment(HardwareRating.RuntimeSupportUnavailable, "GPU runtime support unavailable — CPU inference", reasons);
        }
        if (ramGb < 15)
        {
            reasons.Add("Under 16 GB of RAM leaves little room for a model next to Windows, an IDE and the agent.");
            return new HardwareAssessment(HardwareRating.MemoryLimited, "Memory limited", reasons);
        }
        reasons.Add("Without a supported GPU, small models (1–4B) on the CPU are the practical option.");
        return new HardwareAssessment(HardwareRating.CpuOnly, "CPU-only", reasons);
    }

    public static string Summary(HardwareProfile hw)
    {
        var gpu = hw.PrimaryGpu;
        return $"{hw.Cpu.Name}; {hw.Memory.TotalBytes / (double)Bytes.GiB:0} GiB RAM; " +
               (gpu is null ? "no GPU" : $"{gpu.Name} {gpu.DedicatedMemoryBytes / (double)Bytes.GiB:0.#} GiB") + $"; {hw.Os.Caption} {hw.Os.Build}";
    }
}
