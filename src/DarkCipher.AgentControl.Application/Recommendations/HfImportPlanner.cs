using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Models;

namespace DarkCipher.AgentControl.Application.Recommendations;

public enum ImportFeasibility { Ready, Limited, Unsupported }

public sealed record HfImportPlan(
    string RepoId,
    ImportFeasibility Feasibility,
    string? Quantization,
    string? FileName,
    long? SizeBytes,
    string? OllamaReference,
    long RequiredDiskBytes,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Warnings,
    string Attribution);

/// <summary>
/// Plans a Hugging Face → Ollama import WITHOUT executing anything from the repository. The import uses Ollama's
/// documented direct pull of GGUF repositories ("hf.co/{owner}/{repo}:{quant}"), so Ollama downloads and verifies the
/// file and builds the model from GGUF metadata; no Modelfile templates, scripts or remote code from the repo are used.
/// The result is always "Compatibility Unverified" until the compatibility suite has been run.
/// </summary>
public static class HfImportPlanner
{
    public static HfImportPlan Plan(ModelSourceDetails details, HardwareProfile hw, long freeDiskBytes, int contextTokens = 16384)
    {
        var repo = details.Entry.Id;
        var warnings = new List<string>();
        HfImportPlan Unsupported(string why) => new(repo, ImportFeasibility.Unsupported, null, null, null, null, 0, [], [why, .. warnings], details.Attribution ?? repo);

        if (details.Entry.Private) return Unsupported("Private repositories are not imported by this version.");
        if (details.Entry.Gated) return Unsupported("Gated repositories need you to accept terms and an authenticated Ollama pull; not supported by this version.");

        var ggufs = details.Files.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (ggufs.Count == 0) return Unsupported("No GGUF files in this repository. Ollama can only import GGUF here; other formats would require conversion tools that this app does not run.");

        var hasSplit = ggufs.Any(f => IsSplit(f.Path));
        var single = ggufs.Where(f => !IsSplit(f.Path) && f.Quantization is not null && !f.Path.Contains("mmproj", StringComparison.OrdinalIgnoreCase)).ToList();
        if (single.Count == 0)
            return Unsupported(hasSplit ? "Only split (multi-part) GGUF files are available; split imports are not supported yet." : "No GGUF file with a recognisable quantisation name.");

        var budget = RecommendationEngine.Budget(hw);
        var limit = budget.HasUsableGpu ? budget.GpuBudgetBytes : budget.RamBudgetBytes;
        var byQuant = single.GroupBy(f => f.Quantization!).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        ModelFile? choice = null;
        foreach (var q in GgufPreference)
        {
            if (!byQuant.TryGetValue(q, out var f) || f.SizeBytes is null) continue;
            var kvGuess = ModelCatalog.EstimateKvBytesPerToken(Math.Max(1, f.SizeBytes.Value / 600_000_000.0)) * contextTokens;
            if (f.SizeBytes.Value + kvGuess + RecommendationEngine.RuntimeOverheadBase <= limit) { choice = f; break; }
        }
        var feasibility = ImportFeasibility.Ready;
        if (choice is null)
        {
            choice = single.Where(f => f.SizeBytes is not null).OrderBy(f => f.SizeBytes).FirstOrDefault();
            if (choice is null) return Unsupported("File sizes are unknown; the import cannot be planned safely.");
            feasibility = ImportFeasibility.Limited;
            warnings.Add("Even the smallest quantisation will not fit in GPU memory; it will run partly from system memory or the CPU (slow).");
        }

        var required = (long)(choice.SizeBytes!.Value * 1.1);
        if (required > freeDiskBytes)
            return Unsupported($"Not enough free disk space: needs about {Bytes.Format(required)}, {Bytes.Format(freeDiskBytes)} free on the model drive.");

        if (details.Entry.License is LicenseClass.Unknown) warnings.Add("Licence not stated or not recognised: review the model card before use.");
        else if (details.Entry.License is LicenseClass.ResearchOnly or LicenseClass.CommercialRestricted) warnings.Add($"Licence '{details.Entry.LicenseName}' restricts use; review it.");
        warnings.Add("Compatibility unverified: chat templates and tool calling depend on the GGUF metadata. Run the compatibility test after import.");

        var reference = $"hf.co/{repo}:{choice.Quantization}";
        return new HfImportPlan(repo, feasibility, choice.Quantization, choice.Path, choice.SizeBytes, reference, required,
        [
            $"Ollama downloads {choice.Path} ({Bytes.Format(choice.SizeBytes)}) directly from Hugging Face and verifies its digest.",
            "Ollama builds the model from the GGUF file's own metadata (no repository scripts or templates are run).",
            "This app confirms the model is listed, then offers the compatibility test (text, JSON, tool calls).",
        ], warnings, details.Attribution ?? repo);
    }

    private static readonly string[] GgufPreference = ["Q4_K_M", "Q5_K_M", "Q4_K_S", "Q6_K", "Q5_K_S", "Q8_0", "IQ4_XS", "Q4_0", "Q3_K_M", "IQ3_M", "Q3_K_L", "MXFP4", "Q2_K"];

    private static bool IsSplit(string path) => System.Text.RegularExpressions.Regex.IsMatch(path, @"-0*\d+-of-0*\d+\.gguf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
