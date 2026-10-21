using DarkCipher.AgentControl.Domain.Common;

namespace DarkCipher.AgentControl.Domain.Downloads;

/// <summary>
/// Stages of one queue item. A provider reports Preparing/Downloading/Verifying/Importing/Completed; the queue then adds
/// Installing/Configuring/Testing and the final verdict. An item is never "Ready" just because its bytes arrived.
/// </summary>
public enum DownloadPhase
{
    Queued, Resolving, Preparing, Downloading, Verifying, Importing, Installing, Configuring, Testing,
    /// <summary>Provider finished (bytes verified); post-install testing has not run yet.</summary>
    Completed,
    Ready, ReadyWithLimitations, Failed, Cancelled, RollbackRequired,
    /// <summary>Paused by the user (supported only by providers that resume, e.g. Ollama pulls keep finished layers).</summary>
    Paused,
    /// <summary>Was running when the app last closed; never resumed automatically.</summary>
    Interrupted,
}

public enum QueueItemKind { ModelPull, HuggingFaceImport, GgufDownload, AgentTool, ModelRuntime, Gateway, Extension, TemplatePackage }

public sealed record DownloadRequest(string ProviderId, string Target, string DisplayName, long? ExpectedBytes = null)
{
    public QueueItemKind Kind { get; init; } = QueueItemKind.ModelPull;
    public string Source { get; init; } = "";
    public string? VersionOrTag { get; init; }
    /// <summary>Install queue items: fingerprint of the plan the user approved. Re-checked right before execution.</summary>
    public string? ApprovedPlanFingerprint { get; init; }
    /// <summary>Run the agentic compatibility suite after a model arrives (Ready only if it passes).</summary>
    public bool VerifyForAgents { get; init; }
}

public sealed record DownloadProgress(DownloadPhase Phase, string Status, long? BytesDone, long? BytesTotal);

/// <summary>Port: something that can fetch and verify an artefact (e.g. an Ollama pull, which verifies layer digests).</summary>
public interface IDownloadProvider
{
    string Id { get; }
    /// <summary>True when the provider supports resuming after cancel (Ollama pulls resume from completed layers).</summary>
    bool SupportsResume { get; }
    Task<OperationResult> ExecuteAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken ct);
}

public enum ItemVerdict { Ready, ReadyWithLimitations, Failed }

/// <summary>Result of the Testing stage: what was proven, and where the item ended up.</summary>
public sealed record ItemVerification(ItemVerdict Verdict, string Summary, IReadOnlyList<string> Checks, string? FinalLocation = null, string? Label = null);

/// <summary>Port: post-install testing for one provider's items (runtime visibility, inference, tool use, version probe…).</summary>
public interface IInstalledItemVerifier
{
    bool Handles(DownloadRequest request);
    Task<ItemVerification> VerifyAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken ct);
}
