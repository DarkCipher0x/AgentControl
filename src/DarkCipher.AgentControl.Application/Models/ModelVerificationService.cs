using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Runtimes;

namespace DarkCipher.AgentControl.Application.Models;

/// <summary>
/// The Testing stage for models (pipeline steps 13-19): confirm the runtime lists the model, read its metadata, then —
/// when asked — run the compatibility suite (basic inference, instructions, structured output, tool calling) and save the
/// result for the exact digest. Verdicts:
/// Ready = agentic tests passed; Ready with Limitations = visible but not tested (or tested with limits);
/// "Text Generation Available / Agentic Compatibility Failed" = generation works, tools or JSON do not; Failed = not usable.
/// </summary>
public sealed class ModelVerificationService : IInstalledItemVerifier
{
    private readonly IModelRuntime _runtime;
    private readonly ICompatibilitySuite? _suite;
    private readonly CompatibilityStore _store;
    private readonly HardwareService? _hardware;

    public ModelVerificationService(IModelRuntime runtime, CompatibilityStore store, ICompatibilitySuite? suite = null, HardwareService? hardware = null)
    {
        _runtime = runtime;
        _store = store;
        _suite = suite;
        _hardware = hardware;
    }

    public bool Handles(DownloadRequest request) =>
        request.ProviderId == _runtime.Id && request.Kind is QueueItemKind.ModelPull or QueueItemKind.HuggingFaceImport;

    public Task<ItemVerification> VerifyAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken ct) =>
        VerifyModelAsync(request.Target, request.VerifyForAgents, progress, ct);

    public async Task<ItemVerification> VerifyModelAsync(string model, bool runAgentTests, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var checks = new List<string>();
        progress?.Report(new DownloadProgress(DownloadPhase.Testing, "Confirming the runtime lists the model…", null, null));
        var models = await _runtime.ListModelsAsync(ct).ConfigureAwait(false);
        var m = models.FirstOrDefault(x => Same(x.Name, model));
        if (m is null) return new ItemVerification(ItemVerdict.Failed, $"{model} is not listed by {_runtime.DisplayName} after the download.", checks);
        checks.Add($"Listed by {_runtime.DisplayName}: {m.Name} ({m.ParameterSize} {m.Quantization}, digest {Short(m.Digest)})");

        var shown = await _runtime.ShowAsync(m.Name, ct).ConfigureAwait(false);
        if (shown is null) return new ItemVerification(ItemVerdict.Failed, "The runtime could not read the model's metadata.", checks, m.Name);
        checks.Add($"Metadata: context {shown.ContextLength?.ToString("N0") ?? "unknown"}; capabilities {shown.Capabilities}");

        if (!runAgentTests || _suite is null)
            return new ItemVerification(ItemVerdict.ReadyWithLimitations,
                $"{m.Name} is installed and visible to {_runtime.DisplayName}. Agent compatibility has not been tested yet — run the compatibility test before relying on it.",
                checks, m.Name, "Ready with limitations (not tested)");

        progress?.Report(new DownloadProgress(DownloadPhase.Testing, "Running the compatibility suite (inference, JSON, tool calls)…", null, null));
        var hw = _hardware is null ? "unknown hardware" : Hardware.HardwareAssessor.Summary(await _hardware.GetAsync(ct).ConfigureAwait(false));
        var report = await _suite.RunAsync(m.Name, hw, 16384, new Progress<string>(s => progress?.Report(new DownloadProgress(DownloadPhase.Testing, "Test: " + s, null, null))), ct)
            .ConfigureAwait(false);
        _store.Add(report);
        checks.AddRange(report.Results.Select(r => $"{(r.Passed ? "PASS" : "FAIL")} {r.Test}: {r.Detail}"));
        return report.Verdict switch
        {
            CompatibilityVerdict.AgenticCompatible => new ItemVerification(ItemVerdict.Ready,
                $"{m.Name}: inference, structured output and tool calling passed (suite {report.SuiteVersion}).", checks, m.Name, "Ready"),
            CompatibilityVerdict.TextGenerationOnly => new ItemVerification(ItemVerdict.ReadyWithLimitations,
                $"{m.Name}: Text Generation Available / Agentic Compatibility Failed.", checks, m.Name, "Text Generation Available / Agentic Compatibility Failed"),
            _ => new ItemVerification(ItemVerdict.Failed, $"{m.Name}: the compatibility suite failed (basic generation did not work).", checks, m.Name),
        };
    }

    private static string Short(string? digest) => digest is null ? "unknown" : digest.Length > 12 ? digest[..12] : digest;

    private static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(a, b + ":latest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(a + ":latest", b, StringComparison.OrdinalIgnoreCase);
}
