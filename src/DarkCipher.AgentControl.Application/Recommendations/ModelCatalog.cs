using DarkCipher.AgentControl.Domain.Models;

namespace DarkCipher.AgentControl.Application.Recommendations;

/// <summary>A candidate model for recommendations.</summary>
/// <param name="KvBytesPerToken">f16 KV-cache bytes per context token (2 × layers × KV heads × head dim × 2), from the
/// published architecture. Used for conservative memory estimates; runtimes with KV quantisation use less.</param>
public sealed record CatalogModel(
    string OllamaName,
    string DisplayName,
    string Family,
    double ParametersBillions,
    string Quantization,
    long ApproxSizeBytes,
    int MaxContext,
    ModelCapabilities Capabilities,
    bool CodingSpecialised,
    LicenseClass License,
    string LicenseName,
    long KvBytesPerToken,
    string Notes,
    bool SizeIsApproximate = true);

/// <summary>
/// Curated starting catalogue of well-known Ollama library models. Sizes are APPROXIMATE (from the Ollama library listing
/// at the time of writing) and are always shown as such; the exact size is reported by Ollama during the pull and by the
/// runtime once installed. Capabilities listed here are what the library advertises — agent compatibility is only ever
/// established by running the compatibility suite.
/// </summary>
public static class ModelCatalog
{
    private const long GB = 1_000_000_000L;
    private const ModelCapabilities ChatTools = ModelCapabilities.Completion | ModelCapabilities.Tools;

    public static IReadOnlyList<CatalogModel> Curated { get; } =
    [
        new("gpt-oss:20b", "gpt-oss 20B", "gpt-oss", 20.9, "MXFP4", 14 * GB, 131072, ChatTools | ModelCapabilities.Thinking, false,
            LicenseClass.OpenSource, "apache-2.0", 24_576, "OpenAI open-weight reasoning model with strong tool use (half its layers use a 128-token sliding window, so its KV cache is small)."),
        new("qwen3-coder:30b", "Qwen3-Coder 30B (A3B MoE)", "qwen3moe", 30.5, "Q4_K_M", 19 * GB, 262144, ChatTools, true,
            LicenseClass.OpenSource, "apache-2.0", 98_304, "Coding-specialised mixture-of-experts; needs >16 GB to stay fully on GPU."),
        new("devstral:24b", "Devstral 24B", "mistral", 23.6, "Q4_K_M", 14 * GB, 131072, ChatTools, true,
            LicenseClass.OpenSource, "apache-2.0", 163_840, "Agentic software-engineering model from Mistral AI."),
        new("qwen2.5-coder:32b", "Qwen2.5-Coder 32B", "qwen2", 32.8, "Q4_K_M", 20 * GB, 32768, ChatTools, true,
            LicenseClass.OpenSource, "apache-2.0", 262_144, "Strong coder; too large for a 16 GB GPU without offloading."),
        new("qwen2.5-coder:14b", "Qwen2.5-Coder 14B", "qwen2", 14.8, "Q4_K_M", 9 * GB, 32768, ChatTools, true,
            LicenseClass.OpenSource, "apache-2.0", 196_608, "Good coding quality for 12–16 GB GPUs."),
        new("qwen2.5-coder:7b", "Qwen2.5-Coder 7B", "qwen2", 7.6, "Q4_K_M", 4_700_000_000, 32768, ChatTools, true,
            LicenseClass.OpenSource, "apache-2.0", 57_344, "Fast coder for 8 GB GPUs or as a utility model."),
        new("qwen3:14b", "Qwen3 14B", "qwen3", 14.8, "Q4_K_M", 9_300_000_000, 40960, ChatTools | ModelCapabilities.Thinking, false,
            LicenseClass.OpenSource, "apache-2.0", 163_840, "General model with thinking and tools; 40K context maximum."),
        new("qwen3:8b", "Qwen3 8B", "qwen3", 8.2, "Q4_K_M", 5_200_000_000, 40960, ChatTools | ModelCapabilities.Thinking, false,
            LicenseClass.OpenSource, "apache-2.0", 147_456, "Balanced small model with tools."),
        new("qwen3:4b", "Qwen3 4B", "qwen3", 4.0, "Q4_K_M", 2_500_000_000, 40960, ChatTools | ModelCapabilities.Thinking, false,
            LicenseClass.OpenSource, "apache-2.0", 147_456, "Small model with tools; CPU-friendly."),
        new("llama3.2:3b", "Llama 3.2 3B", "llama", 3.2, "Q4_K_M", 2 * GB, 131072, ChatTools, false,
            LicenseClass.CommercialRestricted, "llama3.2", 114_688, "Very small general model; Llama community licence."),
        new("gemma3:12b", "Gemma 3 12B", "gemma3", 12.2, "Q4_K_M", 8_100_000_000, 131072, ModelCapabilities.Completion | ModelCapabilities.Vision, false,
            LicenseClass.CommercialRestricted, "gemma", 393_216, "Vision + text. Ollama does not list tool support: not agent compatible."),
        new("gemma3:4b", "Gemma 3 4B", "gemma3", 4.3, "Q4_K_M", 3_300_000_000, 131072, ModelCapabilities.Completion | ModelCapabilities.Vision, false,
            LicenseClass.CommercialRestricted, "gemma", 139_264, "Small vision + text model. No tool support: chat only."),
        new("nomic-embed-text", "Nomic Embed Text", "nomic-bert", 0.137, "F16", 274_000_000, 8192, ModelCapabilities.Embedding, false,
            LicenseClass.OpenSource, "apache-2.0", 0, "Embedding model for search/retrieval; not a chat model."),
    ];

    /// <summary>Fallback KV estimate for models without architecture data (≈12 KB per billion parameters per token —
    /// deliberately on the high side).</summary>
    public static long EstimateKvBytesPerToken(double parametersBillions) => (long)(12_000 * Math.Max(0.5, parametersBillions));

    public static double? ParseParameterSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().ToUpperInvariant();
        var mult = s.EndsWith('B') ? 1.0 : s.EndsWith('M') ? 0.001 : double.NaN;
        if (double.IsNaN(mult)) return null;
        return double.TryParse(s[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v * mult : null;
    }

    /// <summary>Builds a catalogue entry from an installed model (exact size from the runtime).</summary>
    public static CatalogModel FromInstalled(ModelDescriptor m)
    {
        var curated = Curated.FirstOrDefault(c => string.Equals(c.OllamaName, m.Name, StringComparison.OrdinalIgnoreCase));
        var p = ParseParameterSize(m.ParameterSize) ?? curated?.ParametersBillions ?? 7;
        return new CatalogModel(m.Name, m.Name, m.Family ?? curated?.Family ?? "unknown", p, m.Quantization ?? "unknown", m.SizeBytes ?? curated?.ApproxSizeBytes ?? 0,
            m.ContextLength ?? curated?.MaxContext ?? 8192, m.Capabilities, curated?.CodingSpecialised ?? m.Name.Contains("coder", StringComparison.OrdinalIgnoreCase),
            curated?.License ?? m.License, curated?.LicenseName ?? m.LicenseName ?? "unknown",
            curated?.KvBytesPerToken ?? EstimateKvBytesPerToken(p), "Installed in Ollama.", SizeIsApproximate: false);
    }
}
