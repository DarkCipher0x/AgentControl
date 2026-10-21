using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Models;

namespace DarkCipher.AgentControl.Application.Recommendations;

public enum RecommendationProfile
{
    FastAndLightweight,
    Balanced,
    BestQualityForMyHardware,
    LowMemory,
    CpuFriendly,
    MaximumPrivacy,
    AgenticCoding,
    CodeReview,
    LargeRepositoryWork,
    Experimental,
    CloudAssisted,
}

public enum MemoryFit { FullyOnGpu, PartialGpuOffload, CpuOnly, DoesNotFit }

/// <param name="Measured">True when the figure is calibrated from the runtime's own report of the model loaded on this PC.</param>
public sealed record MemoryEstimate(long WeightsBytes, long KvCacheBytes, long OverheadBytes, int ContextTokens, bool Measured = false)
{
    public long TotalBytes => WeightsBytes + KvCacheBytes + OverheadBytes;
}

public sealed record MemoryBudget(long GpuBudgetBytes, long RamBudgetBytes, bool HasUsableGpu, IReadOnlyList<string> Assumptions);

public sealed record Recommendation(
    CatalogModel Model,
    RecommendationProfile Profile,
    MemoryEstimate Estimate,
    MemoryFit Fit,
    bool Installed,
    IReadOnlyList<string> CompatibleAgents,
    string Reason,
    IReadOnlyList<string> Limitations,
    double Score);

/// <summary>
/// Recommends models from real constraints rather than parameter count: estimated weights + KV cache for the context the
/// profile needs + runtime overhead, against a GPU budget (dedicated VRAM minus a reserve for the desktop and other GPU
/// users) and a RAM budget (total minus Windows, IDE, compiler, agent and browser headroom). Capabilities (tools,
/// context), licence and privacy are filters, not afterthoughts. No speed figures are produced here: tokens/second only
/// ever come from benchmarks actually run on this machine.
/// </summary>
public sealed class RecommendationEngine
{
    public const long GpuReserveBytes = 1024L * 1024 * 1024;        // desktop compositor, browser, other GPU apps
    public const long WindowsAndToolsReserveBytes = 8L * Bytes.GiB;  // Windows, IDE, compiler, agent CLI, browser
    public const long RuntimeOverheadBase = 512L * 1024 * 1024;     // runtime + compute buffers (base)

    public static MemoryBudget Budget(HardwareProfile hw)
    {
        var gpu = hw.PrimaryGpu;
        var usableGpu = gpu is not null && gpu.Vendor is GpuVendor.Amd or GpuVendor.Nvidia && gpu.DedicatedMemoryBytes >= 3 * Bytes.GiB;
        var gpuBudget = usableGpu ? Math.Max(0, gpu!.DedicatedMemoryBytes - GpuReserveBytes) : 0;
        var ramBudget = Math.Max(hw.Memory.TotalBytes / 2, hw.Memory.TotalBytes - WindowsAndToolsReserveBytes);
        var assumptions = new List<string>
        {
            usableGpu ? $"GPU budget = {Bytes.FormatGiB(gpu!.DedicatedMemoryBytes)} dedicated − {Bytes.FormatGiB(GpuReserveBytes)} reserve for the desktop and other apps."
                      : "No supported GPU: estimates assume CPU inference.",
            $"RAM budget = {Bytes.FormatGiB(hw.Memory.TotalBytes)} − {Bytes.FormatGiB(WindowsAndToolsReserveBytes)} for Windows, IDE, compilers, the agent and a browser.",
            "KV cache estimated at 16-bit precision (worst case); runtimes with KV quantisation or flash attention use less.",
        };
        return new MemoryBudget(gpuBudget, ramBudget, usableGpu, assumptions);
    }

    public static MemoryEstimate Estimate(CatalogModel m, int contextTokens, RunningModel? observed = null)
    {
        var ctx = Math.Min(contextTokens, m.MaxContext);
        var kv = m.KvBytesPerToken * (long)ctx;
        if (observed is { SizeBytes: > 0, ContextLength: > 0 })
        {
            // Calibrate from what the runtime actually loaded on this PC: (measured − its KV share) + KV for the new context.
            var baseBytes = Math.Max(m.ApproxSizeBytes / 2, observed.SizeBytes - m.KvBytesPerToken * (long)observed.ContextLength.Value);
            return new MemoryEstimate(baseBytes, kv, 0, ctx, Measured: true);
        }
        var overhead = RuntimeOverheadBase + (long)(m.ApproxSizeBytes * 0.02);
        return new MemoryEstimate(m.ApproxSizeBytes, kv, overhead, ctx);
    }

    public static MemoryFit Fit(MemoryEstimate e, MemoryBudget b)
    {
        if (b.HasUsableGpu && e.TotalBytes <= b.GpuBudgetBytes) return MemoryFit.FullyOnGpu;
        if (b.HasUsableGpu && e.TotalBytes <= b.GpuBudgetBytes + b.RamBudgetBytes) return MemoryFit.PartialGpuOffload;
        if (!b.HasUsableGpu && e.TotalBytes <= b.RamBudgetBytes) return MemoryFit.CpuOnly;
        return MemoryFit.DoesNotFit;
    }

    public static int ContextFor(RecommendationProfile p) => p switch
    {
        RecommendationProfile.FastAndLightweight or RecommendationProfile.LowMemory or RecommendationProfile.CpuFriendly => 8192,
        RecommendationProfile.AgenticCoding => 32768,
        RecommendationProfile.CodeReview => 32768,
        RecommendationProfile.LargeRepositoryWork => 65536,
        _ => 16384,
    };

    public IReadOnlyList<Recommendation> Recommend(HardwareProfile hw, RecommendationProfile profile, IEnumerable<ModelDescriptor> installed, int max = 5,
        IReadOnlyList<RunningModel>? running = null)
    {
        if (profile == RecommendationProfile.CloudAssisted) return [];

        var installedList = installed.ToList();
        running ??= [];
        var candidates = ModelCatalog.Curated.ToDictionary(c => c.OllamaName, StringComparer.OrdinalIgnoreCase);
        foreach (var m in installedList.Where(m => m.SizeBytes > 0)) candidates[m.Name] = ModelCatalog.FromInstalled(m);

        var budget = Budget(hw);
        var ctx = ContextFor(profile);
        var results = new List<Recommendation>();

        foreach (var m in candidates.Values)
        {
            if (m.Capabilities.HasFlag(ModelCapabilities.Embedding) && !m.Capabilities.HasFlag(ModelCapabilities.Completion)) continue;

            var needsTools = profile is RecommendationProfile.AgenticCoding or RecommendationProfile.LargeRepositoryWork or RecommendationProfile.Balanced
                or RecommendationProfile.BestQualityForMyHardware or RecommendationProfile.CodeReview;
            if (needsTools && !m.Capabilities.HasFlag(ModelCapabilities.Tools)) continue;
            if (profile is RecommendationProfile.LargeRepositoryWork && m.MaxContext < 65536) continue;
            if (profile is RecommendationProfile.AgenticCoding or RecommendationProfile.CodeReview && m.MaxContext < 32768) continue;
            if (profile is RecommendationProfile.CpuFriendly && m.ParametersBillions > 4.5) continue;
            if (profile is RecommendationProfile.MaximumPrivacy && m.License == LicenseClass.Unknown) continue;

            var observed = running.FirstOrDefault(r => string.Equals(r.Name, m.OllamaName, StringComparison.OrdinalIgnoreCase)
                                                       || string.Equals(r.Name, m.OllamaName + ":latest", StringComparison.OrdinalIgnoreCase));
            var est = Estimate(m, ctx, observed);
            var fit = Fit(est, budget);
            if (fit == MemoryFit.DoesNotFit) continue;
            if (fit == MemoryFit.PartialGpuOffload && profile is not (RecommendationProfile.Experimental or RecommendationProfile.BestQualityForMyHardware)) continue;
            if (profile == RecommendationProfile.BestQualityForMyHardware && fit == MemoryFit.PartialGpuOffload && budget.HasUsableGpu) continue;

            var fraction = budget.HasUsableGpu ? est.TotalBytes / (double)Math.Max(1, budget.GpuBudgetBytes) : est.TotalBytes / (double)Math.Max(1, budget.RamBudgetBytes);
            var score = profile switch
            {
                RecommendationProfile.FastAndLightweight or RecommendationProfile.LowMemory or RecommendationProfile.CpuFriendly => 1 - fraction,
                RecommendationProfile.BestQualityForMyHardware or RecommendationProfile.Experimental => m.ParametersBillions / 40.0 + fraction * 0.2,
                RecommendationProfile.AgenticCoding or RecommendationProfile.CodeReview or RecommendationProfile.LargeRepositoryWork =>
                    m.ParametersBillions / 40.0 + (m.CodingSpecialised ? 0.25 : 0) + (m.Capabilities.HasFlag(ModelCapabilities.Thinking) ? 0.05 : 0) - Math.Max(0, fraction - 0.92),
                _ => 1 - Math.Abs(0.6 - fraction),
            };
            var isInstalled = installedList.Any(i => string.Equals(i.Name, m.OllamaName, StringComparison.OrdinalIgnoreCase));
            if (isInstalled) score += 0.08; // no download needed

            results.Add(new Recommendation(m, profile, est, fit, isInstalled, Agents(m), Reason(m, est, fit, budget, profile), Limitations(m, fit, est, ctx), score));
        }

        return results.OrderByDescending(r => r.Score).Take(max).ToList();
    }

    private static List<string> Agents(CatalogModel m) => m.Capabilities.HasFlag(ModelCapabilities.Tools)
        ? ["Codex CLI (local, Ollama)", "Claude Code via local gateway (Experimental)"]
        : ["None: no tool calling — chat only"];

    private static string Reason(CatalogModel m, MemoryEstimate e, MemoryFit fit, MemoryBudget b, RecommendationProfile p)
    {
        var where = fit switch
        {
            MemoryFit.FullyOnGpu => $"fits entirely in GPU memory ({Bytes.FormatGiB(e.TotalBytes)} of {Bytes.FormatGiB(b.GpuBudgetBytes)} budget)",
            MemoryFit.PartialGpuOffload => $"needs {Bytes.FormatGiB(e.TotalBytes)}, more than the {Bytes.FormatGiB(b.GpuBudgetBytes)} GPU budget, so part runs from system memory (slower)",
            _ => $"runs on the CPU within the {Bytes.FormatGiB(b.RamBudgetBytes)} RAM budget",
        };
        var basis = e.Measured
            ? $"calibrated from Ollama's report of this model loaded on this PC; KV cache ≈{Bytes.FormatGiB(e.KvCacheBytes)}"
            : $"weights {Bytes.FormatGiB(e.WeightsBytes)}{(m.SizeIsApproximate ? " approx." : "")}, KV cache ≈{Bytes.FormatGiB(e.KvCacheBytes)}, overhead ≈{Bytes.FormatGiB(e.OverheadBytes)}";
        return $"{m.DisplayName} {where} at {e.ContextTokens:N0} tokens of context ({basis}). " +
               (m.CodingSpecialised ? "Coding-specialised. " : "") + m.Notes;
    }

    private static List<string> Limitations(CatalogModel m, MemoryFit fit, MemoryEstimate e, int ctx)
    {
        var l = new List<string>();
        if (m.SizeIsApproximate) l.Add("Download size is approximate until Ollama reports it.");
        if (fit == MemoryFit.PartialGpuOffload) l.Add("Partly offloaded to system memory: expect much slower generation.");
        if (fit == MemoryFit.CpuOnly) l.Add("CPU inference: expect slow generation; best for short tasks.");
        if (m.MaxContext < ctx) l.Add($"Maximum context is {m.MaxContext:N0} tokens.");
        if (!m.Capabilities.HasFlag(ModelCapabilities.Tools)) l.Add("No tool calling: cannot drive an agent.");
        if (m.License is LicenseClass.CommercialRestricted or LicenseClass.ResearchOnly or LicenseClass.Unknown) l.Add($"Licence '{m.LicenseName}' — review before commercial use.");
        l.Add("Agent compatibility is not verified until the compatibility test passes on this PC.");
        return l;
    }
}
