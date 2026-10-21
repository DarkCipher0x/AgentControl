using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Application.Configuration;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Application.Templates;
using DarkCipher.AgentControl.Application.Verification;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Domain.Templates;
using DarkCipher.AgentControl.Domain.Verification;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Sdk;
using DarkCipher.AgentControl.Security.Configuration;
using DarkCipher.AgentControl.Security.Paths;
using DarkCipher.AgentControl.Security.Permissions;
using DarkCipher.AgentControl.Tests.Application;
using DarkCipher.AgentControl.Tests.Extensions;

namespace DarkCipher.AgentControl.Tests.Verification;

/// <summary>An agent with an end-to-end probe whose answer is scripted.</summary>
public sealed class ProbingAgent(bool installed = true, string version = "1.0.0") : IAgentProvider, IAgentEndToEndProbe
{
    public string Id => "fake";
    public string DisplayName => "Fake agent";
    public string Vendor => "Tests";
    public FeatureMaturity Maturity => FeatureMaturity.Supported;
    public IReadOnlyList<AgentModeInfo> Modes { get; } =
    [
        new(AgentMode.LocalProvider, "Local", FeatureMaturity.Supported, AgentProtocol.OllamaNative, ""),
        new(AgentMode.Native, "Cloud", FeatureMaturity.Supported, AgentProtocol.ProviderNative, ""),
        new(AgentMode.ExperimentalLocalGateway, "Gateway", FeatureMaturity.Experimental, AgentProtocol.AnthropicMessages, ""),
    ];
    public Task<AgentInstallation> DetectAsync(CancellationToken ct) => Task.FromResult(installed
        ? new AgentInstallation(Id, true, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe"), version, ComponentHealth.Healthy, "ok", [])
        : new AgentInstallation(Id, false, null, null, ComponentHealth.Unavailable, "Fake agent is not installed.", []));
    public void InvalidateDetection() { }
    public AgentLaunchPlan BuildLaunchPlan(AgentLaunchRequest r) =>
        new(new ProcessSpec { Executable = r.Installation.ExecutablePath!, WorkingDirectory = r.ProjectDirectory, Mode = ProcessMode.InteractiveConsole, Actor = PathActor.Agent },
            r.Mode, "fake", FeatureMaturity.Supported, [], [], []);
    public IReadOnlyList<PermissionControl> DescribeEnforcement(PermissionProfile profile, AgentMode mode) => [];
    public EndToEndProbe BuildProbe(AgentLaunchRequest r, string token)
    {
        LastToken = token;
        LastPermissions = r.Permissions.Id;
        LastProject = r.ProjectDirectory;
        return new EndToEndProbe(BuildLaunchPlan(r).Process with { Mode = ProcessMode.Captured, Arguments = ["probe", token] }, token, "probe");
    }
    public string? LastToken { get; private set; }
    public string? LastPermissions { get; private set; }
    public string? LastProject { get; private set; }
}

public sealed class StubHardware : IHardwareDiscoveryProvider
{
    public Task<HardwareProfile> ScanAsync(CancellationToken ct) => Task.FromResult(Hw.Profile(16L * 1024 * 1024 * 1024, 64L * 1024 * 1024 * 1024));
}

public class VerificationClassifierTests
{
    private static readonly VerificationEvidence All = new()
    {
        ComponentsInstalled = true, VersionsSupported = true, EndpointsReachable = true, ProtocolsMatch = true, ModelCapabilitiesVerified = true,
        AuthenticationValid = true, ContextSatisfied = true, HardwareSufficient = true, ServicesHealthy = true, ConfigurationGenerated = true,
        ConfigurationValidated = true, EndToEndAttempted = true, EndToEndPassed = true,
    };

    [Fact]
    public void Supported_and_verified_requires_every_condition()
    {
        Assert.Equal(VerificationOutcome.SupportedAndVerified, VerificationClassifier.Classify(All));
        Assert.Equal(VerificationOutcome.Detected, VerificationClassifier.Classify(All with { ComponentsInstalled = false }));
        foreach (var e in new[]
                 {
                     All with { VersionsSupported = false }, All with { EndpointsReachable = false }, All with { ProtocolsMatch = false },
                     All with { AuthenticationValid = false }, All with { ServicesHealthy = false }, All with { ConfigurationGenerated = false },
                     All with { ConfigurationValidated = false }, All with { EndToEndPassed = false },
                 })
            Assert.Equal(VerificationOutcome.FailedValidation, VerificationClassifier.Classify(e));
        Assert.Equal(VerificationOutcome.CompatibleOnPaper, VerificationClassifier.Classify(All with { ModelCapabilitiesVerified = false }));
        Assert.Equal(VerificationOutcome.NotYetTested, VerificationClassifier.Classify(All with { EndToEndAttempted = false, EndToEndPassed = false }));
        Assert.Equal(VerificationOutcome.Experimental, VerificationClassifier.Classify(All with { ExperimentalPath = true }));
        Assert.Equal(VerificationOutcome.ReadyWithLimitations, VerificationClassifier.Classify(All with { HardwareSufficient = false }));
        Assert.Equal(VerificationOutcome.ReadyWithLimitations, VerificationClassifier.Classify(All with { Limitations = ["context"] }));
        Assert.Equal("Supported and Verified", VerificationClassifier.Label(VerificationOutcome.SupportedAndVerified));
    }

    [Fact]
    public void Matrix_records_precise_combinations_and_exports_markdown()
    {
        var m = new SupportedConfigurationMatrix(new MemoryStore());
        m.Record(new SupportedConfigurationEntry
        {
            Label = "s", OperatingSystem = "Windows", AppVersion = "0.1.6", Agent = "Claude Code", AgentVersion = "2.1.296", Runtime = "Ollama", RuntimeVersion = "0.40.6",
            Model = "gpt-oss:20b", Template = "t", TemplateVersion = "1.0.0", Context = 65536, ToolCalling = "passed", StructuredOutput = "passed", InstallResult = "installed",
            LaunchResult = "started", EndToEndResult = "passed", VerifiedAt = DateTimeOffset.UtcNow, Result = VerificationOutcome.Experimental, ConfigurationFingerprint = "abc",
        });
        var md = m.ToMarkdown();
        Assert.Contains("| Experimental | Windows | 0.1.6 | Claude Code 2.1.296 | Ollama 0.40.6 |", md, StringComparison.Ordinal);
    }
}

public class ReadinessTests
{
    private static (StackValidator V, FakeRuntime Rt, FixedPolicy Policy, CompatibilityStore Compat) Make(bool agentInstalled = true)
    {
        var rt = new FakeRuntime();
        var policy = new FixedPolicy(new SecurityPolicy());
        var compat = new CompatibilityStore(new MemoryStore());
        var v = new StackValidator([new ProbingAgent(agentInstalled)], [rt], policy, compat, new PermissionService(new MemoryStore(), new MemoryAudit()), new FakeNetwork());
        return (v, rt, policy, compat);
    }

    private static StackProfile Local(string model = "m:1") => new()
    {
        Id = "s", Name = "S", AgentProviderId = "fake", AgentMode = AgentMode.LocalProvider, RuntimeId = "ollama", Endpoint = "http://127.0.0.1:11434", PrimaryModel = model, ContextLength = 32768,
    };

    [Fact]
    public async Task Missing_agent_runtime_or_model_have_their_own_states_and_fixes()
    {
        var (v, rt, _, _) = Make(agentInstalled: false);
        var r = await v.ValidateAsync(Local(), CancellationToken.None);
        Assert.Equal(StackReadiness.AgentMissing, r.Readiness);
        Assert.Contains(r.Checks, c => c.Fix == CorrectiveAction.InstallAgent);

        (v, rt, _, _) = Make();
        rt.Health = ComponentHealth.Unavailable;
        r = await v.ValidateAsync(Local(), CancellationToken.None);
        Assert.Equal(StackReadiness.RuntimeMissing, r.Readiness);

        rt.Health = ComponentHealth.Healthy;
        r = await v.ValidateAsync(Local(), CancellationToken.None);
        Assert.Equal(StackReadiness.ModelMissing, r.Readiness);
        Assert.Contains(r.Checks, c => c.Fix == CorrectiveAction.PullModel);
    }

    [Fact]
    public async Task Failed_tool_use_and_untested_models_are_distinguished()
    {
        var (v, rt, _, compat) = Make();
        rt.Models.Add(FakeRuntime.Model("m:1"));
        var untested = await v.ValidateAsync(Local(), CancellationToken.None);
        Assert.Equal(StackReadiness.ReadyWithLimitations, untested.Readiness);
        compat.Add(await new FakeSuite(CompatibilityVerdict.TextGenerationOnly).RunAsync("m:1", "hw", 0, null, CancellationToken.None) with { ModelDigest = rt.Models[0].Digest });
        var failed = await v.ValidateAsync(Local(), CancellationToken.None);
        Assert.Equal(StackReadiness.CompatibilityFailed, failed.Readiness);
        compat.Add(await new FakeSuite(CompatibilityVerdict.AgenticCompatible).RunAsync("m:1", "hw", 0, null, CancellationToken.None) with { ModelDigest = rt.Models[0].Digest });
        Assert.Equal(StackReadiness.Ready, (await v.ValidateAsync(Local(), CancellationToken.None)).Readiness);
    }

    [Fact]
    public async Task Cloud_stack_in_local_privacy_mode_is_blocked_by_policy()
    {
        var (v, _, _, _) = Make();
        var r = await v.ValidateAsync(new StackProfile { Id = "c", Name = "C", AgentProviderId = "fake", AgentMode = AgentMode.Native }, CancellationToken.None);
        Assert.Equal(StackReadiness.SecurityPolicyBlocked, r.Readiness);
        Assert.Contains(r.Checks, c => c.Fix == CorrectiveAction.ChangePrivacyMode);
    }

    [Fact]
    public async Task Removed_permission_profile_is_a_permission_conflict()
    {
        var (v, rt, _, _) = Make();
        rt.Models.Add(FakeRuntime.Model("m:1"));
        var r = await v.ValidateAsync(Local() with { PermissionProfileId = "deleted-profile" }, CancellationToken.None);
        Assert.Equal(StackReadiness.PermissionConflict, r.Readiness);
    }

    [Fact]
    public async Task Repair_plans_come_with_evidence_and_never_propose_a_reset()
    {
        var (v, rt, _, _) = Make();
        var stack = Local();
        var report = await v.ValidateAsync(stack, CancellationToken.None);
        var plans = StackRepairService.Diagnose(stack, report);
        var pull = Assert.Single(plans, p => p.Action == CorrectiveAction.PullModel);
        Assert.Contains("m:1", pull.ProposedCorrection, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(pull.Rollback));
        Assert.False(string.IsNullOrWhiteSpace(pull.Validation));
        Assert.DoesNotContain(plans, p => p.ProposedCorrection.Contains("reset", StringComparison.OrdinalIgnoreCase) || p.ProposedCorrection.Contains("reinstall everything", StringComparison.OrdinalIgnoreCase));
    }
}

public class EndToEndVerificationTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private (StackVerificationService Svc, StackService Stacks, ProbingAgent Agent, FakeRuntime Rt, SupportedConfigurationMatrix Matrix, ScriptedExecutor Exec) Make(Func<ProcessSpec, ProcessResult>? answer = null)
    {
        var audit = new MemoryAudit();
        var store = new MemoryStore();
        var paths = new TestPaths(_t.Sub("data"));
        var pps = new ProtectedPathService(ProtectedPathService.DefaultBuiltIns(paths), null, audit);
        var permissions = new PermissionService(store, audit);
        var agent = new ProbingAgent();
        var rt = new FakeRuntime();
        rt.Models.Add(FakeRuntime.Model("m:1", size: 4_700_000_000));   // fits the 16 GB test GPU fully
        var compat = new CompatibilityStore(store);
        var policy = new FixedPolicy(new SecurityPolicy());
        var stacks = new StackService(store, audit, permissions);
        var validator = new StackValidator([agent], [rt], policy, compat, permissions, new FakeNetwork());
        var templates = new TemplateCatalogueService(store, audit, new TemplateValidationService(permissions));
        var tx = new ConfigurationTransactionService(new ConfigurationProtectionService(pps, audit, paths.BackupDirectory), pps, new ConfigurationOwnershipLedger(store), store, audit);
        var exec = new ScriptedExecutor(answer ?? (s => ScriptedExecutor.Ok("The answer: " + s.Arguments.Last())));
        var matrix = new SupportedConfigurationMatrix(store);
        var svc = new StackVerificationService(stacks, validator, [agent], rt, compat, new HardwareService(new StubHardware(), new MemoryLog()), new ConfigurationBuilderService([agent]),
            templates, tx, pps, exec, paths, policy, matrix, audit, "0.1.6-test", new FakeSuite(CompatibilityVerdict.AgenticCompatible));
        stacks.Save(new StackProfile { Id = "local", Name = "Local", AgentProviderId = "fake", AgentMode = AgentMode.LocalProvider, RuntimeId = "ollama",
            Endpoint = "http://127.0.0.1:11434", PrimaryModel = "m:1", ContextLength = 32768 });
        return (svc, stacks, agent, rt, matrix, exec);
    }

    [Fact]
    public async Task Full_verification_runs_suite_and_probe_in_a_disposable_read_only_project()
    {
        var (svc, _, agent, _, matrix, exec) = Make();
        var r = await svc.VerifyAsync("local", new ScriptedApproval(false), null, CancellationToken.None);
        Assert.Equal(VerificationOutcome.SupportedAndVerified, r.Outcome);
        Assert.Equal(BuiltInPermissionProfiles.ReadOnlyId, agent.LastPermissions);
        Assert.Contains("test-projects", agent.LastProject, StringComparison.OrdinalIgnoreCase);
        Assert.Single(exec.Runs);
        var entry = Assert.Single(matrix.Entries);
        Assert.Equal("passed", entry.EndToEndResult);
        Assert.Equal("passed", entry.ToolCalling);
        Assert.Equal("0.1.6-test", entry.AppVersion);
    }

    [Fact]
    public async Task Wrong_answer_is_failed_validation_not_verified()
    {
        var (svc, _, _, _, matrix, _) = Make(_ => ScriptedExecutor.Ok("I refuse"));
        var r = await svc.VerifyAsync("local", new ScriptedApproval(false), null, CancellationToken.None);
        Assert.Equal(VerificationOutcome.FailedValidation, r.Outcome);
        Assert.Equal("failed", matrix.Entries.Single().EndToEndResult);
    }

    [Fact]
    public async Task Missing_model_skips_the_probe()
    {
        var (svc, _, _, rt, _, exec) = Make();
        rt.Models.Clear();
        var r = await svc.VerifyAsync("local", new ScriptedApproval(false), null, CancellationToken.None);
        Assert.NotEqual(VerificationOutcome.SupportedAndVerified, r.Outcome);
        Assert.Empty(exec.Runs);
    }

    [Fact]
    public async Task Cloud_stack_needs_privacy_and_approval_before_any_prompt_is_sent()
    {
        var (svc, stacks, _, _, _, exec) = Make();
        stacks.Save(new StackProfile { Id = "cloud", Name = "Cloud", AgentProviderId = "fake", AgentMode = AgentMode.Native });
        var r = await svc.VerifyAsync("cloud", new ScriptedApproval(true), null, CancellationToken.None);
        Assert.NotEqual(VerificationOutcome.SupportedAndVerified, r.Outcome);   // Fully Local blocks cloud stacks
        Assert.Empty(exec.Runs);
    }
}

public class SdkContributionTests
{
    private static (SdkCapabilityBroker Broker, FakeServices Services, ExtensionContributionRegistry Registry, MemoryAudit Audit) Make(params ExtensionPermission[] granted)
    {
        var audit = new MemoryAudit();
        var services = new FakeServices();
        var registry = new ExtensionContributionRegistry();
        var broker = new SdkCapabilityBroker("com.example.ext", granted.ToHashSet(), services, new FixedPolicy(new SecurityPolicy()), audit, new MemoryLog(),
            new NoExec(), [], new NoHardware(), new NoCreds(), new MemoryStore(), registry, (_, _) => { });
        return (broker, services, registry, audit);
    }

    private sealed class Source : ISdkModelSource
    {
        public string Id => "src";
        public string DisplayName => "Source";
        public Task<IReadOnlyList<SdkModelSearchResult>> SearchAsync(string query, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<SdkModelSearchResult>>([]);
        public Task<SdkModelDetails?> GetDetailsAsync(string id, CancellationToken ct) => Task.FromResult<SdkModelDetails?>(null);
    }

    [Fact]
    public void Catalogue_contributions_need_their_permissions()
    {
        var (b, s, reg, audit) = Make();
        Assert.Throws<CapabilityDeniedException>(() => b.RegisterAgentTool(new SdkAgentToolEntry("x", "X", "P", "", "X.Y", "https://example.org", "MIT", [])));
        Assert.Throws<CapabilityDeniedException>(() => b.RegisterModelSource(new Source()));
        Assert.Throws<CapabilityDeniedException>(() => b.RegisterCompatibilityRule(new SdkCompatibilityRule("r", "", null, null, "warn", "m")));
        Assert.Throws<CapabilityDeniedException>(() => b.RegisterHealthCheck(new SdkHealthCheck("h", "H", _ => Task.FromResult(new SdkHealthResult(SdkHealth.Healthy, "")))));
        Assert.Throws<CapabilityDeniedException>(() => b.RegisterSettingsPanel(new SdkSettingsPanel("p", "P", [])));
        Assert.Empty(s.Tools);
        Assert.Contains(audit.Events, e => e.Action == "ExtensionCapabilityDenied");

        var (b2, _, reg2, _) = Make(ExtensionPermission.CatalogueContributions);
        Assert.Throws<CapabilityDeniedException>(() => b2.RegisterModelSource(new Source()));   // also needs Network
        var (b3, s3, reg3, _) = Make(ExtensionPermission.CatalogueContributions, ExtensionPermission.Network, ExtensionPermission.UI, ExtensionPermission.Settings);
        b3.RegisterAgentTool(new SdkAgentToolEntry("x", "X", "P", "", "X.Y", "https://example.org", "MIT", []));
        b3.RegisterModelSource(new Source());
        b3.RegisterCompatibilityRule(new SdkCompatibilityRule("r", "", "codex", "qwen*", "warn", "Prefer 32K"));
        b3.RegisterSettingsPanel(new SdkSettingsPanel("p", "P", [new SdkSettingField("k", "K", SdkSettingKind.Toggle)]));
        Assert.Single(s3.Tools);
        Assert.Single(reg3.ModelSources);
        Assert.Single(reg3.Rules);
        Assert.Single(reg3.SettingsPanels);
        Assert.Throws<CapabilityDeniedException>(() => b3.RegisterCompatibilityRule(new SdkCompatibilityRule("bad", "", null, null, "execute", "m")));
    }

    [Fact]
    public async Task Extension_health_checks_are_isolated_and_time_limited()
    {
        var (b, _, reg, _) = Make(ExtensionPermission.UI);
        b.RegisterHealthCheck(new SdkHealthCheck("ok", "OK", _ => Task.FromResult(new SdkHealthResult(SdkHealth.Healthy, "fine"))));
        b.RegisterHealthCheck(new SdkHealthCheck("boom", "Boom", _ => throw new InvalidOperationException("broken")));
        b.RegisterHealthCheck(new SdkHealthCheck("slow", "Slow", async ct => { await Task.Delay(TimeSpan.FromMinutes(1), ct); return new SdkHealthResult(SdkHealth.Healthy, ""); }));
        var results = await reg.RunHealthChecksAsync();
        Assert.Equal(SdkHealth.Healthy, results.Single(r => r.Title == "OK").Health);
        Assert.Equal(SdkHealth.Failed, results.Single(r => r.Title == "Boom").Health);
        Assert.Equal(SdkHealth.Failed, results.Single(r => r.Title == "Slow").Health);
    }

    [Fact]
    public void Templates_are_data_only_and_safe_mode_refuses_them()
    {
        var (b, s, _, _) = Make();
        b.RegisterTemplate("{}");
        Assert.Single(s.TemplateJson);
        var policy = new FixedPolicy(new SecurityPolicy { SafeMode = true });
        var safe = new SdkCapabilityBroker("e", new HashSet<ExtensionPermission>(), new FakeServices(), policy, new MemoryAudit(), new MemoryLog(), new NoExec(), [], new NoHardware(), new NoCreds(), new MemoryStore(), new ExtensionContributionRegistry(), (_, _) => { });
        Assert.Throws<CapabilityDeniedException>(() => safe.RegisterTemplate("{}"));
        Assert.True(ExtensionContributionRegistry.Matches("qwen*", "qwen3:14b"));
        Assert.False(ExtensionContributionRegistry.Matches("qwen*", "gpt-oss:20b"));
        Assert.Equal(new Version(1, 1, 0), SdkInfo.Version);
        Assert.True(SdkInfo.IsCompatible(new Version(1, 0, 0)));
    }
}
