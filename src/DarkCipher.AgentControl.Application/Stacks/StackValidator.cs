using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.Application.Stacks;

/// <summary>
/// Checks a stack's readiness without changing anything: agent installed and version, mode, privacy policy, permission
/// profile, endpoint and network policy, runtime health, model presence, tool capability, context, and stored compatibility
/// (and gateway) results. Every failed check names the evidence and the corrective action the UI offers.
/// </summary>
public sealed class StackValidator : IStackValidator
{
    private readonly IReadOnlyList<IAgentProvider> _agents;
    private readonly IReadOnlyList<IModelRuntime> _runtimes;
    private readonly ISecurityPolicyService _policy;
    private readonly CompatibilityStore _compat;
    private readonly IPermissionService? _permissions;
    private readonly INetworkClient? _network;
    private readonly Func<string, string, AgentRequirement?>? _templateRequirement;

    public StackValidator(IEnumerable<IAgentProvider> agents, IEnumerable<IModelRuntime> runtimes, ISecurityPolicyService policy, CompatibilityStore compat,
        IPermissionService? permissions = null, INetworkClient? network = null, Func<string, string, AgentRequirement?>? templateRequirement = null)
    {
        _agents = agents.ToList();
        _runtimes = runtimes.ToList();
        _policy = policy;
        _compat = compat;
        _permissions = permissions;
        _network = network;
        _templateRequirement = templateRequirement;
    }

    public async Task<StackReadinessReport> ValidateAsync(StackProfile stack, CancellationToken ct)
    {
        var checks = new List<ReadinessCheck>();
        var outcome = StackReadiness.Ready;
        void Worst(StackReadiness r) { if (Rank(r) > Rank(outcome)) outcome = r; }
        void Add(string name, bool ok, string detail, CheckSeverity sev = CheckSeverity.Error, CorrectiveAction fix = CorrectiveAction.None, string? evidence = null) =>
            checks.Add(new ReadinessCheck(name, ok, detail, sev) { Fix = ok ? CorrectiveAction.None : fix, Evidence = evidence });

        var agent = _agents.FirstOrDefault(a => a.Id == stack.AgentProviderId);
        if (agent is null)
        {
            Add("Agent", false, $"Unknown agent '{stack.AgentProviderId}'.", fix: CorrectiveAction.InstallAgent);
            return new StackReadinessReport(stack.Id, StackReadiness.ConfigurationInvalid, checks, DateTimeOffset.UtcNow);
        }

        var install = await agent.DetectAsync(ct).ConfigureAwait(false);
        Add("Agent executable", install.Installed, install.Summary, fix: CorrectiveAction.InstallAgent, evidence: install.ExecutablePath ?? "not found");
        if (!install.Installed) Worst(StackReadiness.AgentMissing);
        else
        {
            if (install.Health == ComponentHealth.SecurityBlock) { Add("Agent version", false, install.Summary, evidence: "blocked by the process policy"); Worst(StackReadiness.SecurityPolicyBlocked); }
            else if (install.Version is null) { Add("Agent version", false, "The agent did not report a version.", fix: CorrectiveAction.InstallAgent); Worst(StackReadiness.AgentMissing); }
            else
            {
                var min = stack.TemplateSource is { } src && src.StartsWith("template:", StringComparison.Ordinal) ? _templateRequirement?.Invoke(src["template:".Length..], stack.AgentProviderId)?.MinimumVersion : null;
                if (min is not null && !VersionText.AtLeast(install.Version, min))
                { Add("Agent version", false, $"{install.Version} is older than the template's minimum {min}.", fix: CorrectiveAction.UpdateAgent, evidence: install.Version); Worst(StackReadiness.SetupIncomplete); }
                else Add("Agent version", true, install.Version, CheckSeverity.Info, evidence: install.Version);
            }
        }

        var mode = agent.Modes.FirstOrDefault(m => m.Mode == stack.AgentMode);
        if (mode is null) { Add("Agent mode", false, $"{agent.DisplayName} does not support {stack.AgentMode}."); Worst(StackReadiness.ConfigurationInvalid); }
        else
        {
            Add("Agent mode", true, $"{mode.Label} ({mode.Maturity})", mode.Maturity == FeatureMaturity.Supported ? CheckSeverity.Info : CheckSeverity.Warning);
            if (mode.Maturity != FeatureMaturity.Supported) Worst(StackReadiness.ReadyWithLimitations);
        }

        var policy = _policy.Current;
        var cloud = stack.AgentMode == AgentMode.Native;
        if (cloud && policy.Privacy is PrivacyMode.FullyLocal or PrivacyMode.LocalPreferred)
        {
            Add("Privacy policy", false, $"The app is in {policy.Privacy} mode, but this stack sends prompts and code to the agent vendor's cloud. Change the privacy mode in Security to use it.",
                fix: CorrectiveAction.ChangePrivacyMode, evidence: policy.Privacy.ToString());
            Worst(StackReadiness.SecurityPolicyBlocked);
        }
        else Add("Privacy policy", true, cloud ? "Cloud inference allowed by policy (prompts leave this PC)." : "Inference stays on this PC.", CheckSeverity.Info);

        if (cloud)
        {
            Add("Authentication", true, "The agent signs in with its own account on first launch; this app cannot verify it in advance.", CheckSeverity.Warning);
            Worst(StackReadiness.ReadyWithLimitations);
        }

        if (_permissions is not null && _permissions.Profiles.All(p => p.Id != stack.PermissionProfileId))
        { Add("Permissions", false, $"Permission profile '{stack.PermissionProfileId}' no longer exists.", fix: CorrectiveAction.ChangePermissionProfile); Worst(StackReadiness.PermissionConflict); }
        else Add("Permissions", true, stack.PermissionProfileId, CheckSeverity.Info);

        if (stack.Endpoint is { } ep && Uri.TryCreate(ep, UriKind.Absolute, out var uri) && _network is not null)
        {
            var cls = _network.Classify(uri);
            if (cls == NetworkClass.Loopback) Add("Network policy", true, "Endpoint on this PC (loopback).", CheckSeverity.Info, evidence: uri.GetLeftPart(UriPartial.Authority));
            else if (cls == NetworkClass.PublicInternet && policy.Privacy is PrivacyMode.FullyLocal or PrivacyMode.LocalPreferred)
            { Add("Network policy", false, "The endpoint is on the internet, which this privacy mode blocks.", fix: CorrectiveAction.ChangePrivacyMode, evidence: cls.ToString()); Worst(StackReadiness.SecurityPolicyBlocked); }
            else { Add("Network policy", true, $"{cls} endpoint: requests are allowed only if approved in Security → Network.", CheckSeverity.Warning, evidence: cls.ToString()); Worst(StackReadiness.ReadyWithLimitations); }
        }

        if (stack.RuntimeId is { } rid)
        {
            var runtime = _runtimes.FirstOrDefault(r => r.Id == rid);
            if (runtime is null) { Add("Runtime", false, $"Unknown runtime '{rid}'.", fix: CorrectiveAction.InstallRuntime); Worst(StackReadiness.RuntimeMissing); }
            else
            {
                var status = await runtime.GetStatusAsync(ct).ConfigureAwait(false);
                var ok = status.Health is ComponentHealth.Healthy or ComponentHealth.Degraded;
                var notInstalled = !ok && status.InstallLocation is null && status.Version is null;
                Add("Runtime", ok, status.Summary, ok && status.Health == ComponentHealth.Degraded ? CheckSeverity.Warning : CheckSeverity.Error,
                    notInstalled ? CorrectiveAction.InstallRuntime : CorrectiveAction.StartRuntime, $"{status.Health}; version {status.Version ?? "unknown"}");
                if (!ok) Worst(notInstalled ? StackReadiness.RuntimeMissing : StackReadiness.ServiceUnavailable);
                else if (status.Health == ComponentHealth.Degraded) Worst(StackReadiness.ReadyWithLimitations);

                if (ok && stack.PrimaryModel is { } model)
                {
                    var models = await runtime.ListModelsAsync(ct).ConfigureAwait(false);
                    var m = models.FirstOrDefault(x => Same(x.Name, model));
                    if (m is null) { Add("Primary model", false, $"{model} is not installed.", fix: CorrectiveAction.PullModel); Worst(StackReadiness.ModelMissing); }
                    else
                    {
                        Add("Primary model", true, $"{m.Name} ({m.ParameterSize} {m.Quantization})", CheckSeverity.Info, evidence: m.Digest);
                        if (!m.Supports(ModelCapabilities.Tools))
                        {
                            Add("Tool calling", false, $"{m.Name} does not advertise tool calling; agents cannot edit files or run commands with it.", fix: CorrectiveAction.ChooseCompatibleModel);
                            Worst(StackReadiness.CompatibilityFailed);
                        }
                        var ctx = m.ContextLength;
                        var shown = await runtime.ShowAsync(m.Name, ct).ConfigureAwait(false);
                        ctx = shown?.ContextLength ?? ctx;
                        if (stack.ContextLength is { } want && ctx is { } have && have < want)
                        {
                            Add("Context", false, $"The model's configured context is {have:N0} tokens; the stack wants {want:N0}.", CheckSeverity.Warning, CorrectiveAction.ReduceContext, $"{have} < {want}");
                            Worst(StackReadiness.ReadyWithLimitations);
                        }
                        var report = _compat.Latest(m.Name, m.Digest);
                        if (report is null)
                        {
                            Add("Compatibility", false, "Not tested on this PC yet. Run the compatibility test.", CheckSeverity.Warning, CorrectiveAction.RunCompatibilityTest);
                            Worst(StackReadiness.ReadyWithLimitations);
                        }
                        else if (report.Verdict != CompatibilityVerdict.AgenticCompatible)
                        {
                            Add("Compatibility", false, report.Verdict == CompatibilityVerdict.TextGenerationOnly
                                ? "Text Generation Available / Agentic Compatibility Failed." : "The compatibility test failed.", fix: CorrectiveAction.ChooseCompatibleModel,
                                evidence: $"suite {report.SuiteVersion}, {report.RunAt:yyyy-MM-dd}");
                            Worst(StackReadiness.CompatibilityFailed);
                        }
                        else Add("Compatibility", true, $"Agentic tests passed ({report.RunAt:yyyy-MM-dd}).", CheckSeverity.Info, evidence: $"suite {report.SuiteVersion}");

                        if (stack.AgentMode is AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway)
                        {
                            var gw = _compat.LatestGateway(stack.AgentProviderId, m.Name);
                            if (gw?.Verdict == CompatibilityVerdict.AgenticCompatible) Add("Gateway", true, $"Gateway test passed ({gw.RunAt:yyyy-MM-dd}).", CheckSeverity.Info);
                            else { Add("Gateway", false, gw is null ? "The gateway test has not been run for this model." : "The gateway test failed: " + gw.Detail, CheckSeverity.Warning, CorrectiveAction.RepairGateway); Worst(StackReadiness.ReadyWithLimitations); }
                        }
                    }
                }
            }
        }
        return new StackReadinessReport(stack.Id, outcome, checks, DateTimeOffset.UtcNow);
    }

    private static int Rank(StackReadiness r) => r switch
    {
        StackReadiness.Ready => 0,
        StackReadiness.ReadyWithLimitations => 1,
        StackReadiness.CompatibilityFailed => 2,
        StackReadiness.ModelMissing => 3,
        StackReadiness.ServiceUnavailable => 4,
        StackReadiness.AuthenticationRequired => 5,
        StackReadiness.SetupIncomplete => 6,
        StackReadiness.PermissionConflict => 7,
        StackReadiness.RuntimeMissing => 8,
        StackReadiness.ConfigurationError or StackReadiness.ConfigurationInvalid => 9,
        StackReadiness.AgentMissing => 10,
        StackReadiness.SecurityPolicyBlocked => 11,
        _ => 0,
    };

    internal static bool Same(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || string.Equals(a, b + ":latest", StringComparison.OrdinalIgnoreCase)
        || string.Equals(a + ":latest", b, StringComparison.OrdinalIgnoreCase);
}
