using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.Application.Tools;

/// <summary>
/// Plan → explicit approval → queue. Nothing is installed, updated, repaired or removed without a plan the user approved
/// in a dialog that lists the tool, publisher, source, version, method, downloads, privileges, affected files, environment
/// and PATH changes, services, licence, rollback and warnings. The queue re-plans just before running and refuses to run
/// if the plan changed (see <see cref="AgentToolQueueProvider"/>).
/// </summary>
public sealed class AgentToolInstallationService
{
    private readonly AgentToolCatalogueService _catalogue;
    private readonly DownloadManager _queue;
    private readonly IAuditLogger _audit;

    public AgentToolInstallationService(AgentToolCatalogueService catalogue, DownloadManager queue, IAuditLogger audit)
    {
        _catalogue = catalogue;
        _queue = queue;
        _audit = audit;
    }

    public async Task<OperationResult<InstallPlan>> PlanAsync(string toolId, InstallOperation operation, CancellationToken ct)
    {
        var provider = _catalogue.Find(toolId);
        if (provider is null) return OperationResult<InstallPlan>.Fail($"Unknown tool '{toolId}'.");
        try { return OperationResult<InstallPlan>.Ok(await provider.PlanAsync(operation, ct).ConfigureAwait(false)); }
        catch (InvalidOperationException ex) { return OperationResult<InstallPlan>.Fail(ex.Message); }
    }

    /// <summary>Asks for approval (headless hosts always deny) and, if approved, queues the operation.</summary>
    public async Task<OperationResult<DownloadJob>> SubmitAsync(InstallPlan plan, IApprovalService approval, CancellationToken ct)
    {
        var correlation = CorrelationId.New();
        if (!plan.CanProceed)
            return OperationResult<DownloadJob>.Fail("Not started: " + string.Join("; ", plan.Prerequisites.Where(p => !p.Met).Select(p => $"{p.Name} — {p.Detail}{(p.Remedy is null ? "" : " " + p.Remedy)}")));

        var verb = plan.Operation switch { InstallOperation.Install => "Install", InstallOperation.Update => "Update", InstallOperation.Repair => "Repair", _ => "Uninstall" };
        var ok = await approval.RequestAsync(new ApprovalRequest($"{verb} {plan.DisplayName}?",
            $"{plan.Publisher} · {plan.Source}. Nothing has been changed yet; review the plan.", plan.ApprovalDetails(), plan.Risk, verb), ct).ConfigureAwait(false);
        if (!ok)
        {
            _audit.Record(new AuditEvent("ToolOperationDeclined", "AgentToolInstallationService", AuditResult.Cancelled, plan.ToolId, SecurityClassification.Low, correlation, plan.Operation.ToString()));
            return OperationResult<DownloadJob>.Fail("Not started (not approved).");
        }
        _audit.Record(new AuditEvent("ToolOperationApproved", "AgentToolInstallationService", AuditResult.Approved, plan.ToolId, plan.Risk, correlation,
            $"{plan.Operation} {plan.Version} via {plan.Source}; plan {plan.Fingerprint}"));

        var kind = _catalogue.Find(plan.ToolId)?.Descriptor.Kind switch
        {
            ToolKind.ModelRuntime => QueueItemKind.ModelRuntime,
            ToolKind.Gateway => QueueItemKind.Gateway,
            _ => QueueItemKind.AgentTool,
        };
        return _queue.Enqueue(new DownloadRequest(AgentToolQueueProvider.Id_, AgentToolQueueProvider.Target(plan.ToolId, plan.Operation), $"{verb} {plan.DisplayName}",
            plan.Operation is InstallOperation.Install or InstallOperation.Update ? plan.EstimatedDiskBytes : null)
        {
            Kind = kind,
            Source = plan.Source,
            VersionOrTag = plan.Version,
            ApprovedPlanFingerprint = plan.Fingerprint,
        });
    }
}

/// <summary>
/// Runs approved tool operations inside the unified queue. Immediately before running it builds the plan again and
/// compares fingerprints: if anything that would run or change differs from what was approved (package, version, command,
/// privileges, environment, files), nothing runs and the user is asked to plan again.
/// </summary>
public sealed class AgentToolQueueProvider : IDownloadProvider
{
    public const string Id_ = "agent-tools";
    private readonly AgentToolCatalogueService _catalogue;
    private readonly IAuditLogger _audit;

    public AgentToolQueueProvider(AgentToolCatalogueService catalogue, IAuditLogger audit)
    {
        _catalogue = catalogue;
        _audit = audit;
    }

    public string Id => Id_;
    public bool SupportsResume => false;

    public static string Target(string toolId, InstallOperation op) => $"tool:{toolId}:{op}";

    public static (string ToolId, InstallOperation Op)? ParseTarget(string target)
    {
        var parts = target.Split(':');
        return parts.Length == 3 && parts[0] == "tool" && Enum.TryParse<InstallOperation>(parts[2], out var op) ? (parts[1], op) : null;
    }

    public async Task<OperationResult> ExecuteAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (ParseTarget(request.Target) is not { } t) return OperationResult.Fail("Malformed tool queue item.");
        var provider = _catalogue.Find(t.ToolId);
        if (provider is null) return OperationResult.Fail($"Unknown tool '{t.ToolId}'.");
        if (request.ApprovedPlanFingerprint is null) return OperationResult.Fail("No approved plan; nothing was run.");

        progress.Report(new DownloadProgress(DownloadPhase.Resolving, "Re-checking the approved plan…", null, null));
        var plan = await provider.PlanAsync(t.Op, ct).ConfigureAwait(false);
        if (plan.Fingerprint != request.ApprovedPlanFingerprint)
        {
            _audit.Record(new AuditEvent("ToolPlanChanged", "AgentToolQueueProvider", AuditResult.Blocked, t.ToolId, SecurityClassification.Medium,
                Detail: $"approved {request.ApprovedPlanFingerprint}, now {plan.Fingerprint}"));
            return OperationResult.Fail("The plan changed after you approved it (for example a new version was published). Nothing was run; plan it again to review the change.");
        }
        if (!plan.CanProceed) return OperationResult.Fail("A prerequisite is no longer met: " + string.Join("; ", plan.Prerequisites.Where(p => !p.Met).Select(p => p.Detail)));

        var outcome = await provider.ExecuteAsync(plan, progress, ct).ConfigureAwait(false);
        _audit.Record(new AuditEvent("ToolOperation", "AgentToolQueueProvider", outcome.Success ? AuditResult.Success : AuditResult.Failure, t.ToolId, plan.Risk,
            Detail: $"{plan.Operation}: {outcome.Message}"));
        return outcome.Success ? OperationResult.Ok(outcome.Message) : OperationResult.Fail(outcome.Message);
    }
}

/// <summary>Testing stage for tool operations: detect again, check the version against the supported and verified range.</summary>
public sealed class ToolInstallVerifier : IInstalledItemVerifier
{
    private readonly AgentToolCatalogueService _catalogue;

    public ToolInstallVerifier(AgentToolCatalogueService catalogue) => _catalogue = catalogue;

    public bool Handles(DownloadRequest request) => request.ProviderId == AgentToolQueueProvider.Id_;

    public async Task<ItemVerification> VerifyAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (AgentToolQueueProvider.ParseTarget(request.Target) is not { } t || _catalogue.Find(t.ToolId) is not { } provider)
            return new ItemVerification(ItemVerdict.Failed, "Unknown tool.", []);
        progress.Report(new DownloadProgress(DownloadPhase.Testing, "Detecting the tool and probing its version…", null, null));
        var s = await provider.DetectAsync(ct).ConfigureAwait(false);
        var d = provider.Descriptor;
        var checks = new List<string> { $"Detected: {s.Summary}" };

        if (t.Op is InstallOperation.Uninstall or InstallOperation.Rollback)
            return s.Installed ? new ItemVerification(ItemVerdict.Failed, $"{d.DisplayName} is still detected after removal.", checks)
                               : new ItemVerification(ItemVerdict.Ready, $"{d.DisplayName} is no longer installed.", checks, Label: "Removed");

        if (!s.Installed || s.Version is null)
            return new ItemVerification(ItemVerdict.Failed, $"{d.DisplayName} was not detected (or reported no version) after the operation.", checks);
        if (s.Health is not ComponentHealth.Healthy)
            return new ItemVerification(ItemVerdict.Failed, $"{d.DisplayName} is installed but unhealthy: {s.Summary}", checks, s.ExecutablePath);
        checks.Add($"Version probe: {s.Version}");
        if (d.MinimumSupportedVersion is not null && !VersionText.AtLeast(s.Version, d.MinimumSupportedVersion))
            return new ItemVerification(ItemVerdict.ReadyWithLimitations, $"{s.Version} is older than the minimum supported {d.MinimumSupportedVersion}.", checks, s.ExecutablePath);
        if (d.LatestVerifiedVersion is null)
            return new ItemVerification(ItemVerdict.ReadyWithLimitations, $"{d.DisplayName} {s.Version} works (version probe), but this app has not verified this tool end to end yet.", checks, s.ExecutablePath);
        if (VersionText.Compare(s.Version, d.LatestVerifiedVersion) > 0)
            return new ItemVerification(ItemVerdict.ReadyWithLimitations, $"{d.DisplayName} {s.Version} works (version probe); it is newer than the version this app verified ({d.LatestVerifiedVersion}). Run a stack verification.", checks, s.ExecutablePath);
        return new ItemVerification(ItemVerdict.Ready, $"{d.DisplayName} {s.Version} is installed, healthy and within the verified range.", checks, s.ExecutablePath);
    }
}

/// <summary>Update checks: installed version vs. what the package source offers now.</summary>
public sealed class AgentToolUpdateService
{
    private readonly AgentToolCatalogueService _catalogue;
    public AgentToolUpdateService(AgentToolCatalogueService catalogue) => _catalogue = catalogue;

    public async Task<IReadOnlyList<ToolEntry>> CheckAsync(CancellationToken ct) =>
        (await _catalogue.RefreshAsync(checkAvailable: true, ct).ConfigureAwait(false)).Where(e => e.UpdateAvailable).ToList();
}

public sealed record ToolDiagnosis(string ToolId, bool NeedsRepair, string Issue, string Evidence, InstallPlan? Plan);

/// <summary>Repair diagnoses first and only proposes a plan when something is actually wrong (never a reinstall of a
/// healthy tool, never a reset of the user's configuration).</summary>
public sealed class AgentToolRepairService
{
    private readonly AgentToolCatalogueService _catalogue;
    public AgentToolRepairService(AgentToolCatalogueService catalogue) => _catalogue = catalogue;

    public async Task<ToolDiagnosis> DiagnoseAsync(string toolId, CancellationToken ct)
    {
        var p = _catalogue.Find(toolId);
        if (p is null) return new ToolDiagnosis(toolId, false, "Unknown tool.", "", null);
        var s = await p.DetectAsync(ct).ConfigureAwait(false);
        var evidence = $"State {s.State}; health {s.Health}; version {s.Version ?? "none"}; path {s.ExecutablePath ?? "none"}; {s.Summary}";
        if (!s.Installed) return new ToolDiagnosis(toolId, false, "Not installed — use Install, not Repair.", evidence, null);
        var broken = s.State == ToolInstallState.Broken || s.Health is ComponentHealth.Degraded or ComponentHealth.Crashed or ComponentHealth.Misconfigured || s.Version is null;
        if (!broken) return new ToolDiagnosis(toolId, false, "No problem found; nothing to repair.", evidence, null);
        if (!p.Descriptor.CanInstall) return new ToolDiagnosis(toolId, true, s.Summary, evidence, null);
        var plan = await p.PlanAsync(InstallOperation.Repair, ct).ConfigureAwait(false);
        return new ToolDiagnosis(toolId, true, s.Summary, evidence, plan);
    }
}
