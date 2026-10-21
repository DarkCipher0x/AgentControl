using System.Text.Json;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Runtimes;

namespace DarkCipher.AgentControl.Integrations.Ollama;

/// <summary>Ollama as an <see cref="IModelRuntime"/> and as the download provider for model pulls (Ollama verifies
/// layer digests itself and resumes partially-downloaded layers).</summary>
public sealed class OllamaRuntime : IModelRuntime, IDownloadProvider
{
    public const string RuntimeId = "ollama";
    public static readonly Uri DefaultEndpoint = new("http://127.0.0.1:11434");

    private readonly OllamaClient _client;
    private readonly INetworkClient _net;
    private readonly OllamaDetector _detector;

    public OllamaRuntime(INetworkClient net, OllamaDetector detector, Uri? endpoint = null)
    {
        _net = net;
        _detector = detector;
        _client = new OllamaClient(net, endpoint ?? DefaultEndpoint);
    }

    public string Id => RuntimeId;
    public string DisplayName => "Ollama";
    public FeatureMaturity Maturity => FeatureMaturity.Supported;
    public OllamaClient Client => _client;
    bool IDownloadProvider.SupportsResume => true;

    public async Task<RuntimeStatus> GetStatusAsync(CancellationToken ct)
    {
        var install = _detector.FindInstallation();
        var cls = _net.Classify(_client.BaseUri);
        var endpoint = new RuntimeEndpoint(_client.BaseUri, cls, RuntimeAuth.None, cls == NetworkClass.Loopback ? RuntimeTrust.Trusted : RuntimeTrust.Unverified);
        try
        {
            var version = await _client.VersionAsync(ct).ConfigureAwait(false);
            if (version is null)
                return new RuntimeStatus(Id, ComponentHealth.Misconfigured, RuntimeConnection.Unavailable, null, endpoint, install, "Something answered on the Ollama port but it did not behave like Ollama.");
            var exposure = _detector.CheckExposure(_client.BaseUri.Port);
            var health = exposure is null ? ComponentHealth.Healthy : ComponentHealth.Degraded;
            var summary = exposure ?? $"Ollama {version} is running on this PC only.";
            return new RuntimeStatus(Id, health, cls == NetworkClass.Loopback ? RuntimeConnection.Local : RuntimeConnection.Remote, version, endpoint, install, summary);
        }
        catch (NetworkBlockedException ex)
        {
            return new RuntimeStatus(Id, ComponentHealth.SecurityBlock, RuntimeConnection.Unavailable, null, endpoint, install, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException)
        {
            var summary = install is null
                ? "Ollama is not installed."
                : "Ollama is installed but not running. Start the Ollama app (or AMD AI Bundle) and check again.";
            return new RuntimeStatus(Id, ComponentHealth.Unavailable, RuntimeConnection.Unavailable, null, endpoint, install, summary);
        }
    }

    public async Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken ct)
    {
        var tags = await _client.TagsAsync(ct).ConfigureAwait(false);
        return tags.Select(ToDescriptor).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<RunningModel>> ListRunningAsync(CancellationToken ct)
    {
        var ps = await _client.PsAsync(ct).ConfigureAwait(false);
        return ps.Select(p => new RunningModel(p.Name, p.Size, p.SizeVram, p.ContextLength, p.ExpiresAt)).ToList();
    }

    public async Task<ModelDescriptor?> ShowAsync(string model, CancellationToken ct)
    {
        var show = await _client.ShowAsync(model, ct).ConfigureAwait(false);
        if (show is null) return null;
        return new ModelDescriptor
        {
            Name = model,
            Family = show.Details?.Family,
            ParameterSize = show.Details?.ParameterSize,
            Quantization = show.Details?.QuantizationLevel,
            ContextLength = ConfiguredContext(show) ?? show.Details?.ContextLength ?? ModelInfoContext(show),
            Capabilities = Caps(show.Capabilities),
            LicenseName = FirstLine(show.License),
            License = LicenseClass.Unknown, // Ollama returns licence TEXT, not an identifier: never guess the class from prose.
            Origin = ModelOrigin.InstalledRuntime,
            Readiness = ModelReadiness.CompatibilityUnverified,
        };
    }

    public async Task<OperationResult> PullAsync(string model, IProgress<PullProgress> progress, CancellationToken ct)
    {
        ModelNameRules.Require(model);
        try
        {
            string? lastStatus = null;
            await foreach (var line in _client.PullStreamAsync(model, ct).ConfigureAwait(false))
            {
                PullLine? p;
                try { p = JsonSerializer.Deserialize<PullLine>(line, OllamaClient.Json); } catch (JsonException) { continue; }
                if (p is null) continue;
                if (!string.IsNullOrEmpty(p.Error)) return OperationResult.Fail("Ollama reported: " + p.Error);
                lastStatus = p.Status;
                progress.Report(new PullProgress(p.Status ?? "", p.Completed, p.Total, p.Digest));
            }
            return lastStatus == "success"
                ? OperationResult.Ok($"{model} downloaded and verified by Ollama.")
                : OperationResult.Fail($"The download ended without confirmation (last status: {lastStatus ?? "none"}). Retry to resume.");
        }
        catch (OperationCanceledException) { return OperationResult.Fail("Cancelled. Completed layers are kept; retry to resume.", "cancelled"); }
        catch (Exception ex) when (ex is HttpRequestException or NetworkBlockedException or TimeoutException or InvalidDataException)
        {
            return OperationResult.Fail("Download failed: " + ex.Message);
        }
    }

    public async Task<OperationResult> DeleteAsync(string model, CancellationToken ct)
    {
        var running = await ListRunningAsync(ct).ConfigureAwait(false);
        if (running.Any(r => string.Equals(r.Name, model, StringComparison.OrdinalIgnoreCase)))
            return OperationResult.Fail($"{model} is loaded right now. Unload it (or stop the sessions using it) before removing it.", "in-use");
        var r = await _client.DeleteAsync(model, ct).ConfigureAwait(false);
        return r.IsSuccess ? OperationResult.Ok($"{model} removed.") : OperationResult.Fail($"Ollama refused the removal (HTTP {r.StatusCode}).");
    }

    public async Task<OperationResult> CopyAsync(string source, string destination, CancellationToken ct)
    {
        var r = await _client.CopyAsync(source, destination, ct).ConfigureAwait(false);
        return r.IsSuccess ? OperationResult.Ok($"Copied to {destination}.") : OperationResult.Fail($"Copy failed (HTTP {r.StatusCode}).");
    }

    public async Task<OperationResult> UnloadAsync(string model, CancellationToken ct)
    {
        var r = await _client.UnloadAsync(model, ct).ConfigureAwait(false);
        return r.IsSuccess ? OperationResult.Ok($"{model} unloaded from memory.") : OperationResult.Fail($"Unload failed (HTTP {r.StatusCode}).");
    }

    async Task<OperationResult> IDownloadProvider.ExecuteAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        progress.Report(new DownloadProgress(DownloadPhase.Preparing, "Contacting Ollama", null, null));
        var result = await PullAsync(request.Target, new Progress<PullProgress>(p =>
        {
            var phase = p.Status switch
            {
                "verifying sha256 digest" => DownloadPhase.Verifying,
                "writing manifest" or "removing any unused layers" => DownloadPhase.Importing,
                "success" => DownloadPhase.Completed,
                _ when p.Total is > 0 => DownloadPhase.Downloading,
                _ => DownloadPhase.Preparing,
            };
            progress.Report(new DownloadProgress(phase, p.Status, p.Completed, p.Total));
        }), ct).ConfigureAwait(false);
        if (!result.Success) return result;

        // Not complete until verified: the model must now be listed by the runtime.
        progress.Report(new DownloadProgress(DownloadPhase.Verifying, "Confirming the model is installed", null, null));
        var models = await ListModelsAsync(ct).ConfigureAwait(false);
        var name = request.Target.Contains(':') ? request.Target : request.Target + ":latest";
        return models.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, request.Target, StringComparison.OrdinalIgnoreCase))
            ? OperationResult.Ok(result.Message)
            : OperationResult.Fail("Ollama reported success but the model is not listed. Check the Ollama log.");
    }

    internal static ModelDescriptor ToDescriptor(OllamaClient.TagModelDto t) => new()
    {
        Name = t.Name,
        Family = t.Details?.Family,
        ParameterSize = t.Details?.ParameterSize,
        Quantization = t.Details?.QuantizationLevel,
        SizeBytes = t.Size,
        ContextLength = t.Details?.ContextLength,
        Capabilities = Caps(t.Capabilities),
        Digest = t.Digest,
        ModifiedAt = t.ModifiedAt,
        Origin = ModelOrigin.InstalledRuntime,
        Readiness = ModelReadiness.CompatibilityUnverified,
    };

    internal static ModelCapabilities Caps(IEnumerable<string>? caps)
    {
        var c = ModelCapabilities.None;
        foreach (var s in caps ?? [])
            c |= s switch
            {
                "completion" => ModelCapabilities.Completion,
                "tools" => ModelCapabilities.Tools,
                "vision" => ModelCapabilities.Vision,
                "embedding" => ModelCapabilities.Embedding,
                "thinking" => ModelCapabilities.Thinking,
                "insert" => ModelCapabilities.Insert,
                _ => ModelCapabilities.None,
            };
        return c;
    }

    /// <summary>num_ctx from the Modelfile parameters (what the model will actually use), if set.</summary>
    internal static int? ConfiguredContext(OllamaClient.ShowDto show)
    {
        foreach (var line in (show.Parameters ?? "").Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] == "num_ctx" && int.TryParse(parts[1], out var n)) return n;
        }
        return null;
    }

    private static int? ModelInfoContext(OllamaClient.ShowDto show) =>
        show.ModelInfo?.FirstOrDefault(kv => kv.Key.EndsWith(".context_length", StringComparison.Ordinal)).Value is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : null;

    private static string? FirstLine(string? s) => s?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } l ? (l.Length > 80 ? l[..80] + "…" : l) : null;

    private sealed record PullLine(string? Status, string? Digest, long? Total, long? Completed, string? Error);
}
