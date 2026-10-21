using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Application.Configuration;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Setup;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Application.Templates;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Configuration;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Domain.Verification;

namespace DarkCipher.AgentControl.Application.Verification;

public sealed record VerificationStep(string Name, bool Ok, string Detail);

public sealed record StackVerificationResult(VerificationOutcome Outcome, IReadOnlyList<VerificationStep> Steps, SupportedConfigurationEntry? Entry)
{
    public string Label => VerificationClassifier.Label(Outcome);
}

/// <summary>
/// "Run full verification" for a stack: readiness → model compatibility (suite, re-used if already run for this digest) →
/// gateway test where needed → deterministic configuration generated and validated → a SAFE end-to-end test: the agent
/// starts headless in a NEW disposable project under the app's test-projects folder, with the Read-Only permission
/// profile, answers a fixed prompt, and its output must contain a random token. Cloud stacks need explicit approval because
/// the prompt goes to the vendor. The result is classified strictly and recorded in the supported-configuration matrix.
/// </summary>
public sealed class StackVerificationService
{
    private readonly StackService _stacks;
    private readonly IStackValidator _validator;
    private readonly IReadOnlyList<IAgentProvider> _agents;
    private readonly IModelRuntime _runtime;
    private readonly ICompatibilitySuite? _suite;
    private readonly Func<string, CancellationToken, Task<(CompatibilityVerdict Verdict, string Detail)>>? _gatewayTest;
    private readonly CompatibilityStore _compat;
    private readonly HardwareService _hardware;
    private readonly ConfigurationBuilderService _builder;
    private readonly TemplateCatalogueService _templates;
    private readonly ConfigurationTransactionService _transactions;
    private readonly IProtectedPathService _paths;
    private readonly IProcessExecutor _processes;
    private readonly IAppPaths _appPaths;
    private readonly ISecurityPolicyService _policy;
    private readonly SupportedConfigurationMatrix _matrix;
    private readonly IAuditLogger _audit;
    private readonly string _appVersion;

    public StackVerificationService(StackService stacks, IStackValidator validator, IEnumerable<IAgentProvider> agents, IModelRuntime runtime,
        CompatibilityStore compat, HardwareService hardware, ConfigurationBuilderService builder, TemplateCatalogueService templates,
        ConfigurationTransactionService transactions, IProtectedPathService paths, IProcessExecutor processes, IAppPaths appPaths,
        ISecurityPolicyService policy, SupportedConfigurationMatrix matrix, IAuditLogger audit, string appVersion,
        ICompatibilitySuite? suite = null, Func<string, CancellationToken, Task<(CompatibilityVerdict, string)>>? gatewayTest = null)
    {
        _stacks = stacks; _validator = validator; _agents = agents.ToList(); _runtime = runtime; _compat = compat; _hardware = hardware;
        _builder = builder; _templates = templates; _transactions = transactions; _paths = paths; _processes = processes; _appPaths = appPaths;
        _policy = policy; _matrix = matrix; _audit = audit; _appVersion = appVersion; _suite = suite; _gatewayTest = gatewayTest;
    }

    public async Task<StackVerificationResult> VerifyAsync(string stackId, IApprovalService approval, IProgress<string>? progress, CancellationToken ct)
    {
        var steps = new List<VerificationStep>();
        void Step(string n, bool ok, string d) { steps.Add(new(n, ok, d)); progress?.Report($"{(ok ? "✓" : "✗")} {n}: {d}"); }
        var stack = _stacks.Find(stackId);
        if (stack is null) return new(VerificationOutcome.FailedValidation, [new("Stack", false, "Not found.")], null);
        var agent = _agents.FirstOrDefault(a => a.Id == stack.AgentProviderId);
        var local = stack.AgentMode is AgentMode.LocalProvider or AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway;
        var evidence = new VerificationEvidence();

        // 1. Readiness (installed components, versions, endpoints, policy, permissions)
        var report = await _validator.ValidateAsync(stack, ct).ConfigureAwait(false);
        var blocking = report.Checks.Where(c => !c.Passed && c.Severity == CheckSeverity.Error).ToList();
        Step("Readiness", blocking.Count == 0, blocking.Count == 0 ? report.Summary : string.Join("; ", blocking.Select(b => $"{b.Name}: {b.Detail}")));
        var install = agent is null ? null : await agent.DetectAsync(ct).ConfigureAwait(false);
        var runtime = local ? await _runtime.GetStatusAsync(ct).ConfigureAwait(false) : null;
        evidence = evidence with
        {
            ComponentsInstalled = install?.Installed == true && (!local || runtime?.Health is ComponentHealth.Healthy or ComponentHealth.Degraded),
            VersionsSupported = !report.Checks.Any(c => c.Name == "Agent version" && !c.Passed),
            EndpointsReachable = !local || runtime?.Health is ComponentHealth.Healthy or ComponentHealth.Degraded,
            ProtocolsMatch = !report.Checks.Any(c => c.Name == "Agent mode" && !c.Passed),
            ServicesHealthy = blocking.Count == 0,
            AuthenticationValid = true,
            ExperimentalPath = stack.AgentMode is AgentMode.ExperimentalLocalGateway or AgentMode.VerifiedLocalGateway,
        };

        // 2. Model capabilities (proven, not advertised)
        string toolStatus = "n/a", structured = "n/a";
        string? digest = null;
        Domain.Models.ModelCapabilities? caps = null;
        if (local && stack.PrimaryModel is { } model && evidence.ComponentsInstalled)
        {
            var models = await _runtime.ListModelsAsync(ct).ConfigureAwait(false);
            var m = models.FirstOrDefault(x => StackValidator.Same(x.Name, model));
            digest = m?.Digest;
            caps = m?.Capabilities;
            var comp = m is null ? null : _compat.Latest(m.Name, m.Digest);
            if (m is not null && comp is null && _suite is not null)
            {
                progress?.Report("Running the compatibility suite…");
                var hw = HardwareAssessor.Summary(await _hardware.GetAsync(ct).ConfigureAwait(false));
                comp = await _suite.RunAsync(m.Name, hw, stack.ContextLength ?? 16384, null, ct).ConfigureAwait(false);
                _compat.Add(comp);
            }
            var ok = comp?.Verdict == CompatibilityVerdict.AgenticCompatible;
            toolStatus = comp is null ? "not tested" : comp.Results.FirstOrDefault(r => r.Test.Contains("tool", StringComparison.OrdinalIgnoreCase)) is { } tr ? (tr.Passed ? "passed" : "failed") : comp.Verdict.ToString();
            structured = comp is null ? "not tested" : comp.Results.FirstOrDefault(r => r.Test.Contains("JSON", StringComparison.OrdinalIgnoreCase) || r.Test.Contains("structured", StringComparison.OrdinalIgnoreCase)) is { } sr ? (sr.Passed ? "passed" : "failed") : comp.Verdict.ToString();
            Step("Model capabilities", ok, comp is null ? "Not tested." : $"{comp.Verdict} (suite {comp.SuiteVersion})");
            evidence = evidence with { ModelCapabilitiesVerified = ok };

            var ctxOk = m?.ContextLength is null || stack.ContextLength is null || m.ContextLength >= stack.ContextLength;
            var hwProfile = await _hardware.GetAsync(ct).ConfigureAwait(false);
            var budget = Recommendations.RecommendationEngine.Budget(hwProfile);
            var fits = m?.SizeBytes is { } sz && budget.HasUsableGpu && sz + Recommendations.RecommendationEngine.RuntimeOverheadBase <= budget.GpuBudgetBytes;
            evidence = evidence with { ContextSatisfied = ctxOk, HardwareSufficient = fits };
            if (!fits) Step("Hardware", true, "The model does not fit fully in GPU memory; it will be slower.");

            if (stack.AgentMode is AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway && m is not null)
            {
                var gw = _compat.LatestGateway(stack.AgentProviderId, m.Name);
                if (gw is null && _gatewayTest is not null)
                {
                    progress?.Report("Running the gateway test…");
                    var (v, d) = await _gatewayTest(m.Name, ct).ConfigureAwait(false);
                    gw = new CompatibilityStore.GatewayResult(stack.AgentProviderId, AgentProtocol.AnthropicMessages, m.Name, m.Digest, v, DateTimeOffset.UtcNow, d);
                    _compat.AddGateway(gw);
                }
                Step("Gateway", gw?.Verdict == CompatibilityVerdict.AgenticCompatible, gw is null ? "Not tested." : $"{gw.Verdict}: {gw.Detail}");
                evidence = evidence with { ProtocolsMatch = evidence.ProtocolsMatch && gw?.Verdict == CompatibilityVerdict.AgenticCompatible };
            }
        }
        else if (!local)
        {
            evidence = evidence with { ModelCapabilitiesVerified = true, ContextSatisfied = true, HardwareSufficient = true };
            Step("Model capabilities", true, "Vendor model (not testable locally).");
        }

        // 3. Deterministic configuration
        GeneratedConfiguration? generated = null;
        var template = stack.TemplateSource is { } src && src.StartsWith("template:", StringComparison.Ordinal) ? _templates.Find(src["template:".Length..]) : null;
        if (template is not null && agent is not null)
        {
            var sel = new Domain.Templates.TemplateSelection
            {
                AgentId = stack.AgentProviderId, AgentMode = stack.AgentMode, Model = stack.PrimaryModel, ContextLength = stack.ContextLength,
                PermissionProfileId = stack.PermissionProfileId, Reasoning = stack.Options?.GetValueOrDefault(TemplateOptions.Reasoning),
            };
            var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (install?.Version is { } av) versions[agent.Id] = VersionText.Parse(av)?.ToString(3) ?? av;
            if (runtime?.Version is { } rv) versions[_runtime.Id] = rv;
            generated = _builder.Generate(new ConfigurationInputs(template, sel, versions, digest, caps), Permission(stack.PermissionProfileId));
            var again = _builder.Generate(new ConfigurationInputs(template, sel, versions, digest, caps), Permission(stack.PermissionProfileId));
            var deterministic = again.Fingerprint == generated.Fingerprint;
            Step("Configuration generated", deterministic, $"Fingerprint {generated.Fingerprint}{(deterministic ? " (reproducible)" : " — NOT reproducible")}.");
            evidence = evidence with { ConfigurationGenerated = deterministic };
        }
        else
        {
            Step("Configuration generated", true, "Hand-made stack: the provider's typed launch plan is used.");
            evidence = evidence with { ConfigurationGenerated = true };
        }
        var errors = _stacks.Validate(stack);
        Step("Configuration validated", errors.Count == 0, errors.Count == 0 ? "Stack profile valid." : string.Join(" ", errors));
        evidence = evidence with { ConfigurationValidated = errors.Count == 0 };

        // 4. Safe end-to-end test
        string e2e = "not run", launch = "not run";
        var ready = evidence.ComponentsInstalled && evidence.ConfigurationValidated && blocking.Count == 0;
        if (ready && agent is IAgentEndToEndProbe prober && install is { Installed: true })
        {
            var allowed = true;
            if (!local)
            {
                allowed = _policy.Current.Privacy is PrivacyMode.Hybrid or PrivacyMode.CloudAllowed && await approval.RequestAsync(new ApprovalRequest(
                    "Send a test prompt to the agent's cloud service?", "End-to-end verification of a cloud stack sends one fixed prompt (no code) to the vendor.",
                    ["Prompt: 'Reply with exactly the word …' (a random token)", "Project: a new, empty sample folder", "Permissions: Read-Only"], SecurityClassification.Medium, "Send test prompt"), ct).ConfigureAwait(false);
                if (!allowed) Step("End-to-end test", false, "Not approved (cloud stack).");
            }
            if (allowed)
            {
                (bool ok, string detail, bool launched) = await RunProbeAsync(stack, prober, install, progress, ct).ConfigureAwait(false);
                e2e = ok ? "passed" : "failed";
                launch = launched ? "started" : "failed to start";
                Step("End-to-end test", ok, detail);
                evidence = evidence with { EndToEndAttempted = true, EndToEndPassed = ok };
            }
        }
        else Step("End-to-end test", false, !ready ? "Skipped: earlier checks failed." : "This agent has no end-to-end probe.");

        // Limitations come from a fresh readiness check: the model tests above may have resolved earlier warnings.
        var final = await _validator.ValidateAsync(stack, ct).ConfigureAwait(false);
        var limitations = final.Checks.Where(c => !c.Passed && c.Severity == CheckSeverity.Warning).Select(c => $"{c.Name}: {c.Detail}").ToList();
        evidence = evidence with { Limitations = limitations };
        var outcome = VerificationClassifier.Classify(evidence);
        var entry = new SupportedConfigurationEntry
        {
            Label = stack.Name,
            OperatingSystem = $"Windows {Environment.OSVersion.Version.Build} x64",
            AppVersion = _appVersion,
            Agent = agent?.DisplayName ?? stack.AgentProviderId,
            AgentVersion = install?.Version,
            Runtime = local ? _runtime.DisplayName : "Vendor cloud",
            RuntimeVersion = runtime?.Version,
            Gateway = stack.AgentMode is AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway ? "Ollama Anthropic-compatible API" : null,
            GatewayVersion = stack.AgentMode is AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway ? runtime?.Version : null,
            Model = stack.PrimaryModel ?? "vendor default",
            ModelDigest = digest,
            Template = template?.DisplayName ?? "(hand-made stack)",
            TemplateVersion = template?.Version ?? "—",
            Context = stack.ContextLength,
            ToolCalling = toolStatus,
            StructuredOutput = structured,
            InstallResult = evidence.ComponentsInstalled ? "installed" : "missing components",
            LaunchResult = launch,
            EndToEndResult = e2e,
            VerifiedAt = DateTimeOffset.UtcNow,
            Limitations = limitations,
            Result = outcome,
            ConfigurationFingerprint = generated?.Fingerprint ?? stack.ConfigurationFingerprint,
        };
        _matrix.Record(entry);
        _audit.Record(new AuditEvent("StackVerified", "StackVerificationService", outcome == VerificationOutcome.SupportedAndVerified ? AuditResult.Success : AuditResult.Failure,
            stack.Id, SecurityClassification.Low, Detail: VerificationClassifier.Label(outcome)));
        return new StackVerificationResult(outcome, steps, entry);
    }

    private static PermissionProfile Permission(string id) => BuiltInPermissionProfiles.All().FirstOrDefault(p => p.Id == id) ?? BuiltInPermissionProfiles.Get(BuiltInPermissionProfiles.DefaultId);

    private async Task<(bool Ok, string Detail, bool Launched)> RunProbeAsync(StackProfile stack, IAgentEndToEndProbe prober, AgentInstallation install, IProgress<string>? progress, CancellationToken ct)
    {
        var project = TestProjectFactory.Create(_appPaths.TestProjectsDirectory);
        var decision = _paths.Evaluate(project, PathOperation.UseAsWorkingDirectory, PathActor.Agent);
        if (decision.Kind != PathDecisionKind.Allow) return (false, "The disposable project folder is not allowed: " + decision.Reason, false);
        var token = "DCAC" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var profileDir = Path.Combine(_appPaths.AgentProfilesDirectory, "verify-" + stack.Id);
        EndToEndProbe probe;
        try
        {
            probe = prober.BuildProbe(new AgentLaunchRequest
            {
                Installation = install, Mode = stack.AgentMode, ProjectDirectory = decision.CanonicalPath,
                Permissions = BuiltInPermissionProfiles.Get(BuiltInPermissionProfiles.ReadOnlyId), Model = stack.PrimaryModel,
                Endpoint = stack.Endpoint is null ? null : new Uri(stack.Endpoint), ContextLength = stack.ContextLength,
                ProfileDirectory = profileDir, Options = stack.Options, SystemInstructions = stack.SystemInstructions,
            }, token);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return (false, "Could not build the probe: " + ex.Message, false); }

        // The probe's profile files go through the same transaction pipeline as a session's.
        var plan = (prober as IAgentProvider)?.BuildLaunchPlan(new AgentLaunchRequest
        {
            Installation = install, Mode = stack.AgentMode, ProjectDirectory = decision.CanonicalPath,
            Permissions = BuiltInPermissionProfiles.Get(BuiltInPermissionProfiles.ReadOnlyId), Model = stack.PrimaryModel,
            Endpoint = stack.Endpoint is null ? null : new Uri(stack.Endpoint), ContextLength = stack.ContextLength, ProfileDirectory = profileDir,
            Options = stack.Options, SystemInstructions = stack.SystemInstructions,
        });
        foreach (var f in plan?.FileChanges ?? [])
        {
            var tx = _transactions.PrepareFromGenerated(f.Path, f.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ConfigFormat.Json : ConfigFormat.TomlSubset, f.NewContent, "Verification profile");
            var done = await _transactions.CommitAsync(tx, null, null, ct).ConfigureAwait(false);
            if (done.State != TransactionState.Committed) return (false, "Could not prepare the verification profile: " + string.Join(" ", done.Steps.Where(s => !s.Ok).Select(s => s.Detail)), false);
        }

        progress?.Report("Starting the agent headless in a disposable project…");
        var r = await _processes.RunAsync(probe.Process, ct).ConfigureAwait(false);
        var launched = r.Outcome is ProcessOutcome.Completed or ProcessOutcome.Failed or ProcessOutcome.TimedOut;
        var ok = r.Succeeded && r.StdOut.Contains(token, StringComparison.OrdinalIgnoreCase);
        var tail = r.StdOut.Trim();
        if (tail.Length > 160) tail = "…" + tail[^160..];
        return (ok, ok ? $"{probe.Description} Answered with the expected token in {r.Duration.TotalSeconds:0.0}s."
                       : $"{r.Outcome} (exit {r.ExitCode?.ToString() ?? "—"}): {(r.Succeeded ? "the answer did not contain the token: " + tail : r.Message)}", launched);
    }
}
