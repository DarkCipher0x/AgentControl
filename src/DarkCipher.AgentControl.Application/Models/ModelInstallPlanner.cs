using DarkCipher.AgentControl.Application.Recommendations;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.Application.Models;

public enum PipelineStepState { Done, Pending, Warning, Blocked }

public sealed record PipelineStep(int Number, string Name, PipelineStepState State, string Detail);

/// <summary>The model installation plan (pipeline steps 1-10) shown before anything is downloaded.</summary>
public sealed record ModelInstallPlan(
    string Reference,
    string Source,
    string DisplayName,
    long? SizeBytes,
    string Licence,
    string HardwareFit,
    IReadOnlyList<PipelineStep> Steps,
    IReadOnlyList<string> Warnings)
{
    public bool CanProceed => Steps.All(s => s.State != PipelineStepState.Blocked);
    public IReadOnlyList<string> ApprovalDetails() =>
        [.. Steps.Select(s => $"{s.Number}. {s.Name}: {s.State} — {s.Detail}"), .. Warnings.Select(w => "⚠ " + w)];
}

/// <summary>
/// Builds the pre-download part of the model pipeline: source and metadata, licence and access, file/tag, runtime and
/// agent requirements, hardware suitability and disk space. Discovery is never treated as compatibility: a model is only
/// "Ready" after the Testing stage (ModelVerificationService) proves it.
/// </summary>
public static class ModelInstallPlanner
{
    public static ModelInstallPlan ForOllamaTag(string reference, CatalogModel? curated, HardwareProfile? hw, long? freeDiskBytes,
        TemplateDefinition? forTemplate = null, bool runtimeHealthy = true)
    {
        var steps = new List<PipelineStep>();
        var warnings = new List<string>();
        var valid = Domain.Runtimes.ModelNameRules.IsValid(reference);
        steps.Add(new(1, "Resolve source", valid ? PipelineStepState.Done : PipelineStepState.Blocked,
            valid ? $"Ollama library tag '{reference}' (registry.ollama.ai, HTTPS; Ollama verifies every layer's SHA-256 digest)." : "Not a valid Ollama model reference."));
        steps.Add(new(2, "Retrieve metadata", curated is null ? PipelineStepState.Warning : PipelineStepState.Done,
            curated is null ? "Not in the curated catalogue: size and capabilities are known only after the pull." : $"{curated.DisplayName}: {curated.Quantization}, about {Bytes.Format(curated.ApproxSizeBytes)} (approximate), context up to {curated.MaxContext:N0}."));
        steps.Add(new(3, "Licence and access", curated is null ? PipelineStepState.Warning : curated.License is LicenseClass.Unknown or LicenseClass.ResearchOnly or LicenseClass.CommercialRestricted ? PipelineStepState.Warning : PipelineStepState.Done,
            curated is null ? "Licence unknown until the model card is read; review it before use." : $"{curated.LicenseName} ({curated.License}). Public; no sign-in needed."));
        steps.Add(new(4, "Identify tag", valid ? PipelineStepState.Done : PipelineStepState.Blocked, valid ? reference : "—"));
        steps.Add(new(5, "Runtime compatibility", runtimeHealthy ? PipelineStepState.Done : PipelineStepState.Blocked,
            runtimeHealthy ? "Ollama is running on this PC." : "Ollama is not running. Start or install it first."));
        AddAgentStep(steps, curated?.Capabilities, curated?.MaxContext, forTemplate);
        var (fitState, fit) = HardwareFit(curated?.ApproxSizeBytes, hw);
        steps.Add(new(7, "Hardware suitability", fitState, fit));
        AddDiskStep(steps, curated?.ApproxSizeBytes, freeDiskBytes);
        steps.Add(new(9, "Show plan", PipelineStepState.Done, "This plan."));
        steps.Add(new(10, "Approval", PipelineStepState.Pending, "Requires your confirmation."));
        if (curated is null) warnings.Add("Unknown models are pulled as named; compatibility is decided only by the tests after the pull.");
        return new ModelInstallPlan(reference, "Ollama library", curated?.DisplayName ?? reference, curated?.ApproxSizeBytes,
            curated?.LicenseName ?? "Unknown", fit, steps, warnings);
    }

    public static ModelInstallPlan ForHuggingFace(HfImportPlan hf, ModelSourceDetails details, HardwareProfile? hw, TemplateDefinition? forTemplate = null, bool runtimeHealthy = true)
    {
        var steps = new List<PipelineStep>();
        var blocked = hf.Feasibility == ImportFeasibility.Unsupported;
        steps.Add(new(1, "Resolve source", PipelineStepState.Done, $"huggingface.co/{details.Entry.Id} (HTTPS)."));
        steps.Add(new(2, "Retrieve metadata", PipelineStepState.Done, $"{details.Files.Count} files; author {details.Entry.Author ?? "unknown"}; pipeline {details.Entry.PipelineTag ?? "unknown"}."));
        steps.Add(new(3, "Licence and access", details.Entry.Gated || details.Entry.Private ? PipelineStepState.Blocked
            : details.Entry.License is LicenseClass.Unknown ? PipelineStepState.Warning : PipelineStepState.Done,
            details.Entry.Gated ? "Gated repository: needs accepted terms and authentication (not supported)." : details.Entry.Private ? "Private repository (not supported)."
            : $"Licence: {details.Entry.LicenseName ?? "not stated"} ({details.Entry.License})."));
        steps.Add(new(4, "Identify supported file", blocked ? PipelineStepState.Blocked : PipelineStepState.Done,
            blocked ? string.Join(" ", hf.Warnings.Take(1)) : $"{hf.FileName} ({hf.Quantization}, {Bytes.Format(hf.SizeBytes)}) → {hf.OllamaReference}"));
        steps.Add(new(5, "Runtime compatibility", !runtimeHealthy ? PipelineStepState.Blocked : blocked ? PipelineStepState.Blocked : PipelineStepState.Done,
            !runtimeHealthy ? "Ollama is not running." : blocked ? "No supported acquisition path (GGUF single file through Ollama)." : "GGUF imported by Ollama's documented hf.co pull; no repository code is run."));
        AddAgentStep(steps, null, null, forTemplate);
        var (fitState, fit) = hf.Feasibility == ImportFeasibility.Limited ? (PipelineStepState.Warning, "Will not fit fully in GPU memory; runs partly on CPU (slow).")
            : HardwareFit(hf.SizeBytes, hw);
        steps.Add(new(7, "Hardware suitability", blocked ? PipelineStepState.Pending : fitState, fit));
        steps.Add(new(8, "Free disk space", blocked ? PipelineStepState.Pending : PipelineStepState.Done, $"About {Bytes.Format(hf.RequiredDiskBytes)} needed."));
        steps.Add(new(9, "Show plan", PipelineStepState.Done, "This plan."));
        steps.Add(new(10, "Approval", PipelineStepState.Pending, "Requires your confirmation."));
        return new ModelInstallPlan(hf.OllamaReference ?? details.Entry.Id, "Hugging Face → Ollama", details.Entry.DisplayName, hf.SizeBytes,
            details.Entry.LicenseName ?? "Unknown", fit, steps, hf.Warnings);
    }

    private static void AddAgentStep(List<PipelineStep> steps, ModelCapabilities? caps, int? maxContext, TemplateDefinition? t)
    {
        if (t is null)
        {
            steps.Add(new(6, "Agent requirements", caps is null ? PipelineStepState.Warning : (caps.Value & ModelCapabilities.Tools) != 0 ? PipelineStepState.Done : PipelineStepState.Warning,
                caps is null ? "Unknown until the tests run." : (caps.Value & ModelCapabilities.Tools) != 0 ? "Advertises tool calling (to be proven by the tests)." : "Does not advertise tool calling: text generation only for agents."));
            return;
        }
        var problems = new List<string>();
        if (t.RequiresToolCalling && caps is { } c && (c & ModelCapabilities.Tools) == 0) problems.Add("the template needs tool calling");
        if (maxContext is { } mc && t.MinimumContext > mc) problems.Add($"the template needs {t.MinimumContext:N0} tokens of context, the model supports {mc:N0}");
        steps.Add(new(6, "Agent requirements", problems.Count == 0 ? (caps is null ? PipelineStepState.Warning : PipelineStepState.Done) : PipelineStepState.Blocked,
            problems.Count == 0 ? $"Meets '{t.DisplayName}' on paper (proven by the tests after download)." : "Not suitable: " + string.Join("; ", problems) + "."));
    }

    private static void AddDiskStep(List<PipelineStep> steps, long? size, long? free)
    {
        if (size is null || free is null) { steps.Add(new(8, "Free disk space", PipelineStepState.Warning, free is null ? "Free space unknown." : $"{Bytes.Format(free)} free; model size unknown.")); return; }
        var need = size.Value + size.Value / 10;
        steps.Add(new(8, "Free disk space", free >= need ? PipelineStepState.Done : PipelineStepState.Blocked,
            free >= need ? $"{Bytes.Format(free)} free; about {Bytes.Format(need)} needed." : $"Not enough space: about {Bytes.Format(need)} needed, {Bytes.Format(free)} free."));
    }

    public static (PipelineStepState, string) HardwareFit(long? sizeBytes, HardwareProfile? hw)
    {
        if (sizeBytes is null || hw is null) return (PipelineStepState.Warning, "Unknown until the model and hardware are known.");
        var budget = RecommendationEngine.Budget(hw);
        if (budget.HasUsableGpu && sizeBytes.Value + RecommendationEngine.RuntimeOverheadBase <= budget.GpuBudgetBytes)
            return (PipelineStepState.Done, $"Fits in GPU memory ({Bytes.Format(budget.GpuBudgetBytes)} usable).");
        if (sizeBytes.Value + RecommendationEngine.RuntimeOverheadBase <= budget.RamBudgetBytes)
            return (PipelineStepState.Warning, "Does not fit fully in GPU memory; part runs from system memory (slower).");
        return (PipelineStepState.Blocked, $"Larger than this PC can hold in memory ({Bytes.Format(budget.RamBudgetBytes)} usable).");
    }
}
