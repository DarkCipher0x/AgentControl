using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Recommendations;
using DarkCipher.AgentControl.Application.Sessions;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Domain.Templates;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.Application.Templates;

/// <summary>A dry run: everything that would happen, checked, with nothing written or started.</summary>
public sealed record TemplateDryRun(TemplateCompatibility Compatibility, GeneratedConfiguration? Configuration, SessionLaunchPreview? Launch, IReadOnlyList<string> Notes);

/// <summary>
/// The bridge from installed components to a working stack. It gathers a live environment snapshot, filters templates to
/// those compatible with the selection, previews and dry-runs the generated configuration, creates the stack, and
/// launches it through the normal session service — so every security control (protected paths, privacy policy,
/// permissions, approvals, secure process execution) still applies. Templates never act on their own.
/// </summary>
public sealed class TemplateEngine
{
    private readonly TemplateCatalogueService _catalogue;
    private readonly ConfigurationBuilderService _builder;
    private readonly IReadOnlyList<IAgentProvider> _agents;
    private readonly IModelRuntime _runtime;
    private readonly CompatibilityStore _compat;
    private readonly ISecurityPolicyService _policy;
    private readonly IPermissionService _permissions;
    private readonly IProtectedPathService _paths;
    private readonly ProjectService _projects;
    private readonly StackService _stacks;
    private readonly SessionService _sessions;
    private readonly HardwareService _hardware;
    private readonly CustomAgentRegistry _custom;
    private readonly Func<long?> _freeDisk;

    public TemplateEngine(TemplateCatalogueService catalogue, ConfigurationBuilderService builder, IEnumerable<IAgentProvider> agents, IModelRuntime runtime,
        CompatibilityStore compat, ISecurityPolicyService policy, IPermissionService permissions, IProtectedPathService paths, ProjectService projects,
        StackService stacks, SessionService sessions, HardwareService hardware, CustomAgentRegistry custom, Func<long?> freeDisk)
    {
        _catalogue = catalogue;
        _builder = builder;
        _agents = agents.ToList();
        _runtime = runtime;
        _compat = compat;
        _policy = policy;
        _permissions = permissions;
        _paths = paths;
        _projects = projects;
        _stacks = stacks;
        _sessions = sessions;
        _hardware = hardware;
        _custom = custom;
        _freeDisk = freeDisk;
    }

    public TemplateCatalogueService Catalogue => _catalogue;

    public async Task<EnvironmentSnapshot> SnapshotAsync(string? projectId, CancellationToken ct)
    {
        var agents = new Dictionary<string, EnvironmentSnapshot.AgentFact>();
        foreach (var a in _agents)
        {
            var d = await a.DetectAsync(ct).ConfigureAwait(false);
            agents[a.Id] = new(a.Id, d.Installed, VersionText.Parse(d.Version)?.ToString(3) ?? d.Version, d.Health == ComponentHealth.Healthy,
                a.Modes.Select(m => m.Mode).ToList(), ToolTrust.BuiltInVerified, d.Summary);
        }
        foreach (var c in _custom.All)
            agents[c.Id] = new(c.Id, c.ValidatedVersion is not null, c.ValidatedVersion, c.Trust == ToolTrust.ValidatedLocally, [AgentMode.Native], c.Trust,
                c.Trust == ToolTrust.ValidatedLocally ? $"Validated {c.ValidatedAt:yyyy-MM-dd}" : "Unverified custom tool");

        var status = await _runtime.GetStatusAsync(ct).ConfigureAwait(false);
        var healthy = status.Health is ComponentHealth.Healthy or ComponentHealth.Degraded;
        var models = healthy ? await _runtime.ListModelsAsync(ct).ConfigureAwait(false) : [];
        var facts = new List<EnvironmentSnapshot.ModelFact>();
        var verdicts = new Dictionary<string, CompatibilityVerdict>(StringComparer.OrdinalIgnoreCase);
        var gateways = new Dictionary<string, CompatibilityVerdict>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in models)
        {
            facts.Add(new(m.Name, m.Capabilities, m.ContextLength, m.SizeBytes, m.Digest));
            verdicts[m.Name] = _compat.Latest(m.Name, m.Digest)?.Verdict ?? CompatibilityVerdict.NotRun;
            gateways[m.Name] = _compat.LatestGateway("claude-code", m.Name)?.Verdict ?? CompatibilityVerdict.NotRun;
        }

        var hw = await _hardware.GetAsync(ct).ConfigureAwait(false);
        var budget = RecommendationEngine.Budget(hw);
        PathDecisionKind? decision = null;
        string? reason = null;
        if (projectId is not null && _projects.Find(projectId) is { } p)
        {
            var dec = _paths.Evaluate(p.Path, PathOperation.UseAsWorkingDirectory, PathActor.Agent);
            decision = dec.Kind;
            reason = dec.Kind == PathDecisionKind.Allow ? p.Path : dec.Reason;
        }
        return new EnvironmentSnapshot
        {
            Agents = agents,
            RuntimeId = _runtime.Id,
            RuntimeHealthy = healthy,
            RuntimeVersion = status.Version,
            Models = facts,
            CompatibilityByModel = verdicts,
            GatewayByModel = gateways,
            Privacy = _policy.Current.Privacy,
            GpuBudgetBytes = budget.HasUsableGpu ? budget.GpuBudgetBytes : null,
            RamBudgetBytes = budget.RamBudgetBytes,
            FreeModelDiskBytes = _freeDisk(),
            PermissionProfiles = _permissions.Profiles.Select(x => x.Id).ToHashSet(),
            ProjectDecision = decision,
            ProjectReason = reason,
        };
    }

    public async Task<IReadOnlyList<(TemplateDefinition Template, TemplateCompatibility Result)>> CompatibleAsync(TemplateSelection selection, CancellationToken ct) =>
        TemplateCompatibilityService.Filter(_catalogue.All, selection, await SnapshotAsync(selection.ProjectId, ct).ConfigureAwait(false));

    public static IReadOnlyDictionary<string, string> Versions(EnvironmentSnapshot env)
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in env.Agents.Values.Where(a => a.Version is not null)) d[a.Id] = a.Version!;
        if (env.RuntimeId is not null && env.RuntimeVersion is not null) d[env.RuntimeId] = env.RuntimeVersion;
        return d;
    }

    /// <summary>Preview: the generated configuration and its fingerprint (nothing saved).</summary>
    public async Task<OperationResult<GeneratedConfiguration>> PreviewAsync(string templateId, TemplateSelection selection, CancellationToken ct)
    {
        var t = _catalogue.Find(templateId);
        if (t is null) return OperationResult<GeneratedConfiguration>.Fail("Template not found.");
        var env = await SnapshotAsync(selection.ProjectId, ct).ConfigureAwait(false);
        return Generate(t, selection, env);
    }

    private OperationResult<GeneratedConfiguration> Generate(TemplateDefinition t, TemplateSelection selection, EnvironmentSnapshot env)
    {
        if (t.Agents.All(a => a.AgentId != selection.AgentId)) return OperationResult<GeneratedConfiguration>.Fail("The template does not support that agent.");
        var model = selection.Model ?? t.DefaultModel;
        var fact = model is null ? null : env.Models.FirstOrDefault(m => TemplateCompatibilityService.Same(m.Name, model));
        var digest = fact?.Digest;
        try
        {
            var gen = _builder.Generate(new ConfigurationInputs(t, selection, Versions(env), digest, fact?.Capabilities), _permissions.Resolve(selection.PermissionProfileId ?? t.PermissionProfileId));
            var errors = _stacks.Validate(gen.Stack);
            return errors.Count == 0 ? OperationResult<GeneratedConfiguration>.Ok(gen) : OperationResult<GeneratedConfiguration>.Fail("Generated configuration is invalid: " + string.Join(" ", errors));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return OperationResult<GeneratedConfiguration>.Fail(ex.Message); }
    }

    /// <summary>Dry run: compatibility + generated configuration + (if possible) the session launch preview. Writes nothing.</summary>
    public async Task<TemplateDryRun> DryRunAsync(string templateId, TemplateSelection selection, CancellationToken ct)
    {
        var t = _catalogue.Find(templateId) ?? throw new KeyNotFoundException("Template not found.");
        var env = await SnapshotAsync(selection.ProjectId, ct).ConfigureAwait(false);
        var compat = TemplateCompatibilityService.Evaluate(t, selection, env);
        var notes = new List<string>();
        var gen = Generate(t, selection, env);
        if (!gen.Success) notes.Add(gen.Message);
        SessionLaunchPreview? launch = null;
        if (gen.Value is { } g && selection.ProjectId is not null && compat.CanLaunch)
        {
            // The session preview needs a saved stack; use an in-memory lookup by saving nothing: preview from the generated profile.
            var existing = _stacks.Find(g.Stack.Id);
            if (existing is not null && existing.ConfigurationFingerprint == g.Fingerprint)
            {
                var preview = await _sessions.PreviewAsync(existing.Id, selection.ProjectId, ct).ConfigureAwait(false);
                if (preview.Success) launch = preview.Value; else notes.Add(preview.Message);
            }
            else notes.Add("Create the stack to see the exact launch command for your project.");
        }
        return new TemplateDryRun(compat, gen.Value, launch, notes);
    }

    /// <summary>Creates (or updates) the stack generated from a template — only when the compatibility check allows it.</summary>
    public async Task<OperationResult<StackProfile>> CreateStackAsync(string templateId, TemplateSelection selection, CancellationToken ct)
    {
        var t = _catalogue.Find(templateId);
        if (t is null) return OperationResult<StackProfile>.Fail("Template not found.");
        var env = await SnapshotAsync(selection.ProjectId, ct).ConfigureAwait(false);
        var compat = TemplateCompatibilityService.Evaluate(t, selection, env);
        var structural = compat.Checks.Where(c => c.Outcome == CheckOutcome.Failed && c.Name is not ("Working Directory" or "Model Installed" or "Disk Capacity")).ToList();
        if (structural.Count > 0)
            return OperationResult<StackProfile>.Fail("Not created: " + string.Join("; ", structural.Select(c => $"{c.Name} — {c.Detail}")));
        var gen = Generate(t, selection, env);
        if (!gen.Success || gen.Value is null) return OperationResult<StackProfile>.Fail(gen.Message);
        var saved = _stacks.Save(gen.Value.Stack);
        return saved.Success ? OperationResult<StackProfile>.Ok(gen.Value.Stack, $"Stack '{gen.Value.Stack.Name}' created (configuration {gen.Value.Fingerprint}).")
                             : OperationResult<StackProfile>.Fail(saved.Message);
    }

    /// <summary>Launch: re-checks compatibility (the Launch button is disabled with reasons when it fails), then starts the
    /// session through the session service with its own approvals.</summary>
    public async Task<OperationResult<Domain.Sessions.SessionInfo>> LaunchAsync(string templateId, TemplateSelection selection, IApprovalService approval, CancellationToken ct)
    {
        if (selection.ProjectId is null) return OperationResult<Domain.Sessions.SessionInfo>.Fail("Choose a project first.");
        var t = _catalogue.Find(templateId);
        if (t is null) return OperationResult<Domain.Sessions.SessionInfo>.Fail("Template not found.");
        var env = await SnapshotAsync(selection.ProjectId, ct).ConfigureAwait(false);
        var compat = TemplateCompatibilityService.Evaluate(t, selection, env);
        if (!compat.CanLaunch)
            return OperationResult<Domain.Sessions.SessionInfo>.Fail("Cannot launch: " + string.Join("; ", compat.Checks.Where(c => c.Outcome == CheckOutcome.Failed).Select(c => $"{c.Name} — {c.Detail}")));
        var stack = await CreateStackAsync(templateId, selection, ct).ConfigureAwait(false);
        if (!stack.Success || stack.Value is null) return OperationResult<Domain.Sessions.SessionInfo>.Fail(stack.Message);
        return await _sessions.StartAsync(stack.Value.Id, selection.ProjectId, approval, ct).ConfigureAwait(false);
    }
}
