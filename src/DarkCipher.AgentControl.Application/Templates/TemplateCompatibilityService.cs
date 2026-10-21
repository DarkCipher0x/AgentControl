using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Templates;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.Application.Templates;

/// <summary>Everything the compatibility check needs, gathered once (live) by the template engine. Pure data, so the
/// rules below are deterministic and unit-testable.</summary>
public sealed record EnvironmentSnapshot
{
    public sealed record AgentFact(string Id, bool Installed, string? Version, bool Healthy, IReadOnlyList<AgentMode> Modes, ToolTrust Trust, string Summary);
    public sealed record ModelFact(string Name, ModelCapabilities Capabilities, int? ContextLength, long? SizeBytes, string? Digest);

    public IReadOnlyDictionary<string, AgentFact> Agents { get; init; } = new Dictionary<string, AgentFact>();
    public string? RuntimeId { get; init; }
    public bool RuntimeHealthy { get; init; }
    public string? RuntimeVersion { get; init; }
    public IReadOnlyList<ModelFact> Models { get; init; } = [];
    /// <summary>Compatibility suite verdicts by model name (latest run for that digest).</summary>
    public IReadOnlyDictionary<string, CompatibilityVerdict> CompatibilityByModel { get; init; } = new Dictionary<string, CompatibilityVerdict>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Claude Code gateway test verdicts by model name.</summary>
    public IReadOnlyDictionary<string, CompatibilityVerdict> GatewayByModel { get; init; } = new Dictionary<string, CompatibilityVerdict>(StringComparer.OrdinalIgnoreCase);
    public PrivacyMode Privacy { get; init; } = PrivacyMode.FullyLocal;
    public long? GpuBudgetBytes { get; init; }
    public long? RamBudgetBytes { get; init; }
    public long? FreeModelDiskBytes { get; init; }
    public IReadOnlySet<string> PermissionProfiles { get; init; } = new HashSet<string>();
    public PathDecisionKind? ProjectDecision { get; init; }
    public string? ProjectReason { get; init; }
    public bool EndpointAllowed { get; init; } = true;
    public string? EndpointReason { get; init; }
    public string OperatingSystem { get; init; } = "windows-x64";
}

/// <summary>
/// Validates a template against a selection and the live environment, check by check, each with a corrective action:
/// agent installed and version, runtime installed/version, gateway, protocol, model installed, loadable, capabilities,
/// tool calling, structured output, context, hardware, disk, permission profile, working directory, required services,
/// authentication, network policy and privacy mode. Launch is allowed only when no check failed.
/// </summary>
public static class TemplateCompatibilityService
{
    private const long Overhead = 1_500_000_000;

    public static TemplateCompatibility Evaluate(TemplateDefinition t, TemplateSelection s, EnvironmentSnapshot env)
    {
        var c = new List<CompatibilityCheck>();
        void Add(string name, CheckOutcome o, string detail, CorrectiveAction fix = CorrectiveAction.None) => c.Add(new(name, o, detail, fix));

        var req = t.Agents.FirstOrDefault(a => a.AgentId == s.AgentId);
        env.Agents.TryGetValue(s.AgentId, out var agent);
        var mode = s.AgentMode ?? (req is { Modes.Count: > 0 } ? req.Modes[0] : AgentMode.Native);
        var local = mode is AgentMode.LocalProvider or AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway;

        // Agent
        if (req is null) Add("Agent Installed", CheckOutcome.Failed, $"This template does not support {s.AgentId}.", CorrectiveAction.ChooseCompatibleModel);
        else if (agent is null || !agent.Installed) Add("Agent Installed", CheckOutcome.Failed, $"{s.AgentId} is not installed.", CorrectiveAction.InstallAgent);
        else if (!agent.Healthy) Add("Agent Installed", CheckOutcome.Failed, agent.Summary, CorrectiveAction.InstallAgent);
        else if (agent.Trust == ToolTrust.Unverified) Add("Agent Installed", CheckOutcome.Failed, "Custom tool not validated yet.", CorrectiveAction.ValidateCustomAgent);
        else Add("Agent Installed", CheckOutcome.Passed, agent.Summary);
        if (req is not null && agent is { Installed: true })
        {
            if (req.MinimumVersion is not null && !VersionText.AtLeast(agent.Version, req.MinimumVersion))
                Add("Agent Version", CheckOutcome.Failed, $"{agent.Version ?? "unknown"} is older than the required {req.MinimumVersion}.", CorrectiveAction.UpdateAgent);
            else Add("Agent Version", CheckOutcome.Passed, agent.Version ?? "unknown");
            if (!req.Modes.Contains(mode) || !agent.Modes.Contains(mode)) Add("API Protocol", CheckOutcome.Failed, $"{mode} is not supported here.", CorrectiveAction.ChooseCompatibleModel);
            else Add("API Protocol", CheckOutcome.Passed, mode.ToString());
        }

        // Runtime and services
        if (t.Runtime is { } rt && local)
        {
            if (env.RuntimeId != rt.RuntimeId || env.RuntimeVersion is null && !env.RuntimeHealthy)
                Add("Runtime Installed", CheckOutcome.Failed, $"{rt.RuntimeId} is not installed or not reachable.", CorrectiveAction.InstallRuntime);
            else if (!env.RuntimeHealthy) Add("Runtime Installed", CheckOutcome.Failed, $"{rt.RuntimeId} is installed but not running.", CorrectiveAction.StartRuntime);
            else Add("Runtime Installed", CheckOutcome.Passed, $"{rt.RuntimeId} {env.RuntimeVersion}");
            if (rt.MinimumVersion is not null && env.RuntimeVersion is not null && !VersionText.AtLeast(env.RuntimeVersion, rt.MinimumVersion))
                Add("Runtime Version", CheckOutcome.Failed, $"{env.RuntimeVersion} is older than {rt.MinimumVersion}.", CorrectiveAction.InstallRuntime);
            else Add("Runtime Version", env.RuntimeVersion is null ? CheckOutcome.Warning : CheckOutcome.Passed, env.RuntimeVersion ?? "unknown");
            Add("Required Services", env.RuntimeHealthy ? CheckOutcome.Passed : CheckOutcome.Failed,
                env.RuntimeHealthy ? string.Join(", ", t.ServiceDependencies) + " running." : "Ollama must be running.", env.RuntimeHealthy ? CorrectiveAction.None : CorrectiveAction.StartRuntime);
        }
        else Add("Runtime Installed", CheckOutcome.NotApplicable, "Cloud agent: no local runtime.");

        // Model
        var modelName = s.Model ?? t.DefaultModel;
        var model = modelName is null ? null : env.Models.FirstOrDefault(m => Same(m.Name, modelName));
        if (local)
        {
            if (modelName is null) Add("Model Installed", CheckOutcome.Failed, "Choose a model.", CorrectiveAction.ChooseCompatibleModel);
            else if (model is null)
            {
                Add("Model Installed", CheckOutcome.Failed, $"{modelName} is not installed.", CorrectiveAction.PullModel);
                Add("Disk Capacity", env.FreeModelDiskBytes is null ? CheckOutcome.Warning : CheckOutcome.Passed,
                    env.FreeModelDiskBytes is null ? "Free space unknown." : $"{Bytes.Format(env.FreeModelDiskBytes)} free for the download.");
            }
            else
            {
                Add("Model Installed", CheckOutcome.Passed, $"{model.Name} ({Bytes.Format(model.SizeBytes)})");
                var fits = model.SizeBytes is { } sz && env.GpuBudgetBytes is { } g && sz + Overhead <= g;
                var loadable = model.SizeBytes is { } sz2 && env.RamBudgetBytes is { } ram ? sz2 + Overhead <= ram : true;
                Add("Model Loaded or Loadable", !loadable ? CheckOutcome.Failed : fits ? CheckOutcome.Passed : CheckOutcome.Warning,
                    !loadable ? "Too large for this PC's memory." : fits ? "Fits in GPU memory." : "Runs partly from system memory (slower).",
                    loadable ? CorrectiveAction.None : CorrectiveAction.ChooseCompatibleModel);
                Add("Hardware Capacity", !loadable ? CheckOutcome.Failed : fits ? CheckOutcome.Passed : CheckOutcome.Warning,
                    env.GpuBudgetBytes is null ? "GPU memory unknown." : $"GPU budget {Bytes.Format(env.GpuBudgetBytes)}.");
                var missing = t.RequiredCapabilities & ~model.Capabilities;
                Add("Model Capability", missing == ModelCapabilities.None ? CheckOutcome.Passed : CheckOutcome.Failed,
                    missing == ModelCapabilities.None ? model.Capabilities.ToString() : $"Missing: {missing}.", missing == ModelCapabilities.None ? CorrectiveAction.None : CorrectiveAction.ChooseCompatibleModel);

                env.CompatibilityByModel.TryGetValue(model.Name, out var verdict);
                if (t.RequiresToolCalling)
                    Add("Tool Calling", verdict switch
                    {
                        CompatibilityVerdict.AgenticCompatible => CheckOutcome.Passed,
                        CompatibilityVerdict.NotRun => CheckOutcome.Warning,
                        _ => CheckOutcome.Failed,
                    }, verdict switch
                    {
                        CompatibilityVerdict.AgenticCompatible => "Proven by the compatibility suite.",
                        CompatibilityVerdict.NotRun => "Advertised but not tested on this PC yet.",
                        CompatibilityVerdict.TextGenerationOnly => "Text Generation Available / Agentic Compatibility Failed.",
                        _ => "The compatibility suite failed.",
                    }, verdict == CompatibilityVerdict.AgenticCompatible ? CorrectiveAction.None : verdict == CompatibilityVerdict.NotRun ? CorrectiveAction.RunCompatibilityTest : CorrectiveAction.ChooseCompatibleModel);
                if (t.RequiresStructuredOutput)
                    Add("Structured Output", verdict == CompatibilityVerdict.AgenticCompatible ? CheckOutcome.Passed : verdict == CompatibilityVerdict.NotRun ? CheckOutcome.Warning : CheckOutcome.Failed,
                        verdict == CompatibilityVerdict.AgenticCompatible ? "Proven by the compatibility suite." : "Not proven.", verdict == CompatibilityVerdict.AgenticCompatible ? CorrectiveAction.None : CorrectiveAction.RunCompatibilityTest);

                if ((s.Reasoning ?? t.Options.GetValueOrDefault(TemplateOptions.Reasoning)) is { } level && (model.Capabilities & ModelCapabilities.Thinking) == 0)
                    Add("Reasoning", CheckOutcome.Warning, $"The '{level}' reasoning level will be left out: {model.Name} does not support thinking.");

                var ctx = s.ContextLength ?? (t.RecommendedContext > 0 ? t.RecommendedContext : t.MinimumContext);
                if (model.ContextLength is { } maxCtx && ctx > maxCtx)
                    Add("Context Support", ctx > 0 && t.MinimumContext <= maxCtx ? CheckOutcome.Warning : CheckOutcome.Failed,
                        $"Requested {ctx:N0} tokens; the model supports {maxCtx:N0}.", CorrectiveAction.ReduceContext);
                else if (ctx < t.MinimumContext) Add("Context Support", CheckOutcome.Failed, $"{ctx:N0} is below the template minimum {t.MinimumContext:N0}.", CorrectiveAction.ReduceContext);
                else Add("Context Support", CheckOutcome.Passed, $"{ctx:N0} tokens.");
            }

            if (mode is AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway && modelName is not null)
            {
                env.GatewayByModel.TryGetValue(modelName, out var gv);
                Add("Gateway Availability", !env.RuntimeHealthy ? CheckOutcome.Failed : gv == CompatibilityVerdict.AgenticCompatible ? CheckOutcome.Passed : CheckOutcome.Warning,
                    !env.RuntimeHealthy ? "Ollama is not running." : gv == CompatibilityVerdict.AgenticCompatible ? "The gateway test passed for this model." : "The gateway test has not passed for this model (Experimental).",
                    gv == CompatibilityVerdict.AgenticCompatible ? CorrectiveAction.None : CorrectiveAction.RepairGateway);
            }
        }

        // Policy, permissions, folders, network
        if (!t.AllowedPrivacy.Contains(env.Privacy)) Add("Privacy Mode", CheckOutcome.Failed, $"The app is in {env.Privacy}; this template allows {string.Join(", ", t.AllowedPrivacy)}.", CorrectiveAction.ChangePrivacyMode);
        else if (!local && env.Privacy is PrivacyMode.FullyLocal or PrivacyMode.LocalPreferred) Add("Privacy Mode", CheckOutcome.Failed, "Cloud agents are blocked in this privacy mode.", CorrectiveAction.ChangePrivacyMode);
        else Add("Privacy Mode", CheckOutcome.Passed, env.Privacy.ToString());
        var profile = s.PermissionProfileId ?? t.PermissionProfileId;
        Add("Permission Profile", env.PermissionProfiles.Contains(profile) ? CheckOutcome.Passed : CheckOutcome.Failed, profile, env.PermissionProfiles.Contains(profile) ? CorrectiveAction.None : CorrectiveAction.ChangePermissionProfile);
        Add("Working Directory", env.ProjectDecision switch
        {
            null => CheckOutcome.Warning,
            PathDecisionKind.Allow => CheckOutcome.Passed,
            PathDecisionKind.RequiresApproval => CheckOutcome.Warning,
            _ => CheckOutcome.Failed,
        }, env.ProjectDecision is null ? "Choose a project before launching." : env.ProjectReason ?? env.ProjectDecision.ToString()!,
            env.ProjectDecision is null or PathDecisionKind.Deny ? CorrectiveAction.ChooseProject : CorrectiveAction.None);
        Add("Network Policy", env.EndpointAllowed ? CheckOutcome.Passed : CheckOutcome.Failed, env.EndpointReason ?? (local ? "Loopback endpoint." : "Vendor endpoint."),
            env.EndpointAllowed ? CorrectiveAction.None : CorrectiveAction.ApproveNetworkDestination);
        Add("Authentication", local ? CheckOutcome.NotApplicable : CheckOutcome.Warning,
            local ? "Local runtime: no account needed." : "The agent asks for its own sign-in on first launch; this app cannot verify it in advance.", local ? CorrectiveAction.None : CorrectiveAction.ConfigureAuthentication);
        if (!t.SupportedOperatingSystems.Contains(env.OperatingSystem)) Add("Operating System", CheckOutcome.Failed, $"{env.OperatingSystem} is not supported by this template.");
        return new TemplateCompatibility(t.Id, t.Version, c);
    }

    /// <summary>Templates worth offering for a selection: right agent and mode, privacy allowed, and (if a model is chosen)
    /// capabilities and context that the model can meet. Each is returned with its full evaluation.</summary>
    public static IReadOnlyList<(TemplateDefinition Template, TemplateCompatibility Result)> Filter(IEnumerable<TemplateDefinition> all, TemplateSelection s, EnvironmentSnapshot env) =>
        all.Where(t => t.Agents.Any(a => a.AgentId == s.AgentId && (s.AgentMode is null || a.Modes.Contains(s.AgentMode.Value))))
           .Where(t => t.AllowedPrivacy.Contains(s.Privacy ?? env.Privacy))
           .Where(t =>
           {
               if (s.Model is null) return true;
               var m = env.Models.FirstOrDefault(x => Same(x.Name, s.Model));
               return m is null || ((t.RequiredCapabilities & ~m.Capabilities) == ModelCapabilities.None && (m.ContextLength is null || m.ContextLength >= t.MinimumContext));
           })
           .Select(t => (t, Evaluate(t, s with { Model = s.Model ?? t.DefaultModel }, env)))
           .OrderBy(x => x.Item2.CanLaunch ? 0 : 1).ThenBy(x => x.t.DisplayName, StringComparer.OrdinalIgnoreCase)
           .ToList();

    internal static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(a, b + ":latest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(a + ":latest", b, StringComparison.OrdinalIgnoreCase);
}
