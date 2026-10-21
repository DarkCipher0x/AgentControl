using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Configuration;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Sessions;
using DarkCipher.AgentControl.Domain.Stacks;

namespace DarkCipher.AgentControl.Application.Sessions;

public sealed record SessionLaunchPreview(AgentLaunchPlan Plan, string CommandPreview, PathDecision ProjectDecision, string? PermissionNote, PermissionProfile EffectiveProfile);

/// <summary>
/// Starts, tracks and stops agent sessions. A launch is: resolve stack + project → protected-path decision for the
/// project (Agent actor) → effective permission profile (read-only folders force Read-Only) → privacy policy check →
/// build plan → write isolated profile files through ConfigurationProtection (only inside the app's agent-profile
/// directory) → explicit approval where required → start via the secure process executor. Only sessions this app
/// started can be stopped.
/// </summary>
public sealed class SessionService
{
    private const string StoreName = "sessions";
    private readonly Stacks.StackService _stacks;
    private readonly ProjectService _projects;
    private readonly IEnumerable<IAgentProvider> _agents;
    private readonly IPermissionService _permissions;
    private readonly IProtectedPathService _paths;
    private readonly IProcessExecutor _processes;
    private readonly IConfigurationProtection _config;
    private readonly ISecurityPolicyService _policy;
    private readonly IAuditLogger _audit;
    private readonly IAppPaths _appPaths;
    private readonly IDocumentStore _store;
    private readonly object _gate = new();
    private readonly List<SessionInfo> _sessions;

    private readonly Configuration.ConfigurationTransactionService? _transactions;

    public SessionService(Stacks.StackService stacks, ProjectService projects, IEnumerable<IAgentProvider> agents, IPermissionService permissions,
        IProtectedPathService paths, IProcessExecutor processes, IConfigurationProtection config, ISecurityPolicyService policy, IAuditLogger audit,
        IAppPaths appPaths, IDocumentStore store, Configuration.ConfigurationTransactionService? transactions = null)
    {
        _transactions = transactions;
        _stacks = stacks;
        _projects = projects;
        _agents = agents;
        _permissions = permissions;
        _paths = paths;
        _processes = processes;
        _config = config;
        _policy = policy;
        _audit = audit;
        _appPaths = appPaths;
        _store = store;
        // History from previous runs: anything that was "running" belonged to an earlier app instance and is not ours.
        _sessions = store.Load(StoreName, () => new Doc()).Sessions
            .Select(s => s.State is SessionState.Running or SessionState.Starting ? s with { State = SessionState.Exited, Message = "Ended with a previous app run (not tracked)." } : s)
            .ToList();
        _processes.ProcessExited += OnProcessExited;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SessionInfo> All { get { lock (_gate) return _sessions.OrderByDescending(s => s.StartedAt).ToList(); } }

    public async Task<OperationResult<SessionLaunchPreview>> PreviewAsync(string stackId, string projectId, CancellationToken ct)
    {
        var stack = _stacks.Find(stackId);
        var project = _projects.Find(projectId);
        if (stack is null) return OperationResult<SessionLaunchPreview>.Fail("Stack not found.");
        if (project is null) return OperationResult<SessionLaunchPreview>.Fail("Project not found.");

        var agent = _agents.FirstOrDefault(a => a.Id == stack.AgentProviderId);
        if (agent is null) return OperationResult<SessionLaunchPreview>.Fail($"Agent '{stack.AgentProviderId}' is not available.");

        var decision = _paths.Evaluate(project.Path, PathOperation.UseAsWorkingDirectory, PathActor.Agent);
        if (decision.Kind == PathDecisionKind.Deny) return OperationResult<SessionLaunchPreview>.Fail(decision.Reason);

        if (stack.AgentMode == AgentMode.Native && _policy.Current.Privacy is PrivacyMode.FullyLocal or PrivacyMode.LocalPreferred)
            return OperationResult<SessionLaunchPreview>.Fail($"This stack sends prompts to the cloud, but the app is in {_policy.Current.Privacy} mode. Change the privacy mode in Security first.");

        var install = await agent.DetectAsync(ct).ConfigureAwait(false);
        if (!install.Installed) return OperationResult<SessionLaunchPreview>.Fail(install.Summary);

        var (effective, note) = _permissions.Effective(_permissions.Resolve(stack.PermissionProfileId), decision);
        AgentLaunchPlan plan;
        try
        {
            plan = agent.BuildLaunchPlan(new AgentLaunchRequest
            {
                Installation = install,
                Mode = stack.AgentMode,
                ProjectDirectory = decision.CanonicalPath,
                Permissions = effective,
                Model = stack.PrimaryModel,
                Endpoint = stack.Endpoint is null ? null : new Uri(stack.Endpoint),
                ContextLength = stack.ContextLength,
                ProfileDirectory = Path.Combine(_appPaths.AgentProfilesDirectory, stack.Id),
                Options = stack.Options,
                SystemInstructions = stack.SystemInstructions,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return OperationResult<SessionLaunchPreview>.Fail(ex.Message);
        }
        return OperationResult<SessionLaunchPreview>.Ok(new SessionLaunchPreview(plan, _processes.Preview(plan.Process), decision, note, effective));
    }

    /// <summary>Starts a session from a preview. <paramref name="approval"/> is asked when the folder needs approval or
    /// the stack is experimental/cloud; a headless host passes <see cref="DenyAllApprovals"/>.</summary>
    public async Task<OperationResult<SessionInfo>> StartAsync(string stackId, string projectId, IApprovalService approval, CancellationToken ct)
    {
        var correlation = CorrelationId.New();
        var previewResult = await PreviewAsync(stackId, projectId, ct).ConfigureAwait(false);
        if (!previewResult.Success || previewResult.Value is null) return OperationResult<SessionInfo>.Fail(previewResult.Message);
        var preview = previewResult.Value;
        var stack = _stacks.Find(stackId)!;
        var project = _projects.Find(projectId)!;

        var details = new List<string> { "Backend: " + preview.Plan.BackendDescription, "Folder: " + preview.ProjectDecision.CanonicalPath, "Permissions: " + preview.EffectiveProfile.Name };
        if (preview.PermissionNote is not null) details.Add(preview.PermissionNote);
        details.AddRange(preview.Plan.Warnings);
        details.Add("Command: " + preview.CommandPreview);

        var needsApproval = preview.ProjectDecision.Kind == PathDecisionKind.RequiresApproval || preview.Plan.Maturity != FeatureMaturity.Supported || stack.AgentMode == AgentMode.Native;
        if (needsApproval)
        {
            var title = preview.ProjectDecision.Kind == PathDecisionKind.RequiresApproval ? "Protected folder — approve session" : $"Start {stack.Name}?";
            var ok = await approval.RequestAsync(new ApprovalRequest(title, preview.ProjectDecision.Kind == PathDecisionKind.RequiresApproval ? preview.ProjectDecision.Reason : preview.Plan.BackendDescription,
                details, preview.ProjectDecision.Kind == PathDecisionKind.RequiresApproval ? SecurityClassification.High : SecurityClassification.Medium, "Start session"), ct).ConfigureAwait(false);
            if (!ok)
            {
                _audit.Record(new AuditEvent("SessionLaunchDeclined", "SessionService", AuditResult.Cancelled, project.Path, SecurityClassification.Low, correlation));
                return OperationResult<SessionInfo>.Fail("Not started.");
            }
            if (preview.ProjectDecision.Kind == PathDecisionKind.RequiresApproval)
                _audit.Record(new AuditEvent("ProtectedPathApproved", "SessionService", AuditResult.Approved, project.Path, SecurityClassification.High, correlation, preview.ProjectDecision.Reason));
        }

        // Isolated profile files: only ever inside the app's agent-profile directory, written as configuration transactions
        // (merge only the generated keys, keep anything else the user added, conflicts stop the launch, verify after write).
        var profileRoot = _paths.Canonicalize(_appPaths.AgentProfilesDirectory);
        foreach (var change in preview.Plan.FileChanges)
        {
            var target = _paths.Canonicalize(change.Path);
            if (!target.StartsWith(profileRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return OperationResult<SessionInfo>.Fail("The launch plan tried to write outside the app's agent-profile folder; refused.");
            if (_transactions is not null && FormatOf(target) is { } format)
            {
                var tx = _transactions.PrepareFromGenerated(target, format, change.NewContent, $"{stack.Name}: {change.Reason}");
                if (tx.Conflicts.Count > 0)
                    return OperationResult<SessionInfo>.Fail($"You changed {string.Join(", ", tx.Conflicts.Select(c => c.Key))} in {target} after this app wrote it. " +
                        "Open Stacks → Configuration to keep your value or use the generated one; nothing was changed.");
                var committed = await _transactions.CommitAsync(tx, null, null, ct).ConfigureAwait(false);
                if (committed.State is not TransactionState.Committed)
                    return OperationResult<SessionInfo>.Fail("Could not prepare the agent profile: " + string.Join(" ", committed.Steps.Where(s => !s.Ok).Select(s => s.Detail)));
                continue;
            }
            var applied = _config.Apply(_config.Preview(target, change.NewContent), correlation);
            if (!applied.Success) return OperationResult<SessionInfo>.Fail("Could not prepare the agent profile: " + applied.Message);
        }

        if (stack.AgentMode == AgentMode.Native)
            _audit.Record(new AuditEvent("CloudInferenceSession", "SessionService", AuditResult.Approved, stack.AgentProviderId, SecurityClassification.Medium, correlation, preview.Plan.BackendDescription));

        var spec = preview.Plan.Process with
        {
            PathApprovalGranted = preview.ProjectDecision.Kind == PathDecisionKind.RequiresApproval,
            DuplicateKey = $"{stack.Id}|{project.Id}",
        };
        var started = _processes.Start(spec);
        var info = new SessionInfo(Guid.NewGuid().ToString("N")[..10], stack.Id, stack.Name, project.Id, preview.ProjectDecision.CanonicalPath,
            started.Process?.Id, started.Process?.ProcessId, DateTimeOffset.UtcNow, started.Success ? SessionState.Running : SessionState.Failed,
            preview.Plan.BackendDescription, preview.CommandPreview) { Message = started.Message };
        lock (_gate)
        {
            _sessions.Add(info);
            Persist();
        }
        _audit.Record(new AuditEvent("SessionStarted", "SessionService", started.Success ? AuditResult.Success : AuditResult.Failure, project.Path, SecurityClassification.Low, correlation,
            $"{stack.Name}; {preview.EffectiveProfile.Name}; {started.Message}"));
        Changed?.Invoke(this, EventArgs.Empty);
        return started.Success ? OperationResult<SessionInfo>.Ok(info, "Session started in its own terminal window.") : OperationResult<SessionInfo>.Fail(started.Message);
    }

    public OperationResult Stop(string sessionId)
    {
        SessionInfo? s;
        lock (_gate) s = _sessions.FirstOrDefault(x => x.Id == sessionId);
        if (s?.TrackedProcessId is null || s.State != SessionState.Running) return OperationResult.Fail("That session is not running under this app.");
        if (!_processes.Stop(s.TrackedProcessId)) return OperationResult.Fail("The process is not owned by this app; nothing was stopped.");
        Update(s.TrackedProcessId, x => x with { State = SessionState.Stopped, EndedAt = DateTimeOffset.UtcNow, Message = "Stopped by you." });
        return OperationResult.Ok("Stopped.");
    }

    public async Task<OperationResult<SessionInfo>> RestartAsync(string sessionId, IApprovalService approval, CancellationToken ct)
    {
        SessionInfo? s;
        lock (_gate) s = _sessions.FirstOrDefault(x => x.Id == sessionId);
        if (s is null) return OperationResult<SessionInfo>.Fail("Session not found.");
        if (s.State == SessionState.Running) Stop(sessionId);
        await Task.Delay(500, ct).ConfigureAwait(false);
        return await StartAsync(s.StackId, s.ProjectId, approval, ct).ConfigureAwait(false);
    }

    private void OnProcessExited(object? sender, TrackedProcess p) =>
        Update(p.Id, s => s.State == SessionState.Running ? s with { State = SessionState.Exited, ExitCode = p.ExitCode, EndedAt = DateTimeOffset.UtcNow } : s);

    private void Update(string trackedId, Func<SessionInfo, SessionInfo> f)
    {
        lock (_gate)
        {
            var i = _sessions.FindIndex(x => x.TrackedProcessId == trackedId);
            if (i < 0) return;
            _sessions[i] = f(_sessions[i]);
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Persist()
    {
        if (_sessions.Count > 200) _sessions.RemoveRange(0, _sessions.Count - 200);
        _store.Save(StoreName, new Doc { Sessions = _sessions });
    }

    private static Domain.Configuration.ConfigFormat? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".toml" => Domain.Configuration.ConfigFormat.TomlSubset,
        ".json" => Domain.Configuration.ConfigFormat.Json,
        _ => null,
    };

    public sealed class Doc
    {
        public int SchemaVersion { get; set; } = 1;
        public List<SessionInfo> Sessions { get; set; } = [];
    }
}

