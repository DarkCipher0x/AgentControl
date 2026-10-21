using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Application.Configuration;
using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Application.Templates;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Application.Verification;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Templates;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Infrastructure.Hardware;
using DarkCipher.AgentControl.Integrations.Agents;
using DarkCipher.AgentControl.Integrations.Ollama;
using DarkCipher.AgentControl.Integrations.Tools;
using DarkCipher.AgentControl.Security.Configuration;
using DarkCipher.AgentControl.Security.Network;
using DarkCipher.AgentControl.Security.Paths;
using DarkCipher.AgentControl.Security.Permissions;
using DarkCipher.AgentControl.Security.Processes;
using DarkCipher.AgentControl.Security.Redaction;
using Xunit.Abstractions;

namespace DarkCipher.AgentControl.Tests.Integrations;

/// <summary>
/// Opt-in, CHANGES THIS PC: installs agent tools through the app's real catalogue path (plan → approval → queue → winget →
/// Testing stage) and runs the real end-to-end stack verification. Approval is given by the person running it.
/// Enable with DCAC_LIVE_INSTALL=1 (DCAC_LIVE_TOOLS = comma-separated tool ids; DCAC_LIVE_MODEL = an installed model).
/// The verification matrix is written to %USERPROFILE%\.dcac-dev\live-matrix.md.
/// </summary>
public class LiveInstallAndVerifyTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();
    private static bool Enabled => Environment.GetEnvironmentVariable("DCAC_LIVE_INSTALL") == "1";
    private static string Model => Environment.GetEnvironmentVariable("DCAC_LIVE_MODEL") ?? "llama3.2:latest";

    private sealed class World
    {
        public required SecureProcessExecutionService Exec { get; init; }
        public required ClaudeCodeProvider Claude { get; init; }
        public required CodexProvider Codex { get; init; }
        public required OllamaRuntime Runtime { get; init; }
        public required AgentToolCatalogueService Catalogue { get; init; }
        public required AgentToolInstallationService Install { get; init; }
        public required StackService Stacks { get; init; }
        public required StackVerificationService Verify { get; init; }
        public required TemplateEngineParts Templates { get; init; }
        public required SupportedConfigurationMatrix Matrix { get; init; }
    }

    public sealed record TemplateEngineParts(TemplateCatalogueService Catalogue, ConfigurationBuilderService Builder);

    private World Build()
    {
        var audit = new MemoryAudit();
        var store = new MemoryStore();
        var log = new MemoryLog();
        var paths = new TestPaths(_t.Sub("data"));
        var pps = new ProtectedPathService(ProtectedPathService.DefaultBuiltIns(paths), null, audit);
        var exec = new SecureProcessExecutionService(pps, new Redactor(), audit, null, log);
        var policy = new FixedPolicy(new SecurityPolicy());
        var runtime = new OllamaRuntime(new NetworkSecurityService(policy, audit, null, null), new OllamaDetector());
        var claude = new ClaudeCodeProvider(exec);
        var codex = new CodexProvider(exec);
        var winget = new WingetClient(exec);
        var custom = new CustomAgentRegistry(store, audit);
        var catalogue = new AgentToolCatalogueService(
            [new ClaudeCodeToolProvider(winget, claude), new CodexToolProvider(winget, codex), new OllamaToolProvider(winget, new OllamaDetector(), runtime)],
            custom, _ => throw new InvalidOperationException());
        var queue = new DownloadManager([new AgentToolQueueProvider(catalogue, audit)], log, audit, () => null, [new ToolInstallVerifier(catalogue)]);
        var permissions = new PermissionService(store, audit);
        var compat = new CompatibilityStore(store);
        var stacks = new StackService(store, audit, permissions);
        var templates = new TemplateCatalogueService(store, audit, new TemplateValidationService(permissions));
        var builder = new ConfigurationBuilderService([claude, codex]);
        var validator = new StackValidator([claude, codex], [runtime], policy, compat, permissions, new NetworkSecurityService(policy, audit, null, null),
            (id, agent) => templates.Find(id)?.Agents.FirstOrDefault(a => a.AgentId == agent));
        var tx = new ConfigurationTransactionService(new ConfigurationProtectionService(pps, audit, paths.BackupDirectory), pps, new ConfigurationOwnershipLedger(store), store, audit);
        var matrix = new SupportedConfigurationMatrix(store);
        var hardware = new HardwareService(new WindowsHardwareDiscoveryProvider(log), log);
        var suite = new OllamaCompatibilitySuite(runtime);
        var probe = new GatewayProbe(runtime);
        var verify = new StackVerificationService(stacks, validator, [claude, codex], runtime, compat, hardware, builder, templates, tx, pps, exec, paths, policy, matrix, audit,
            typeof(StackVerificationService).Assembly.GetName().Version!.ToString(3), suite, async (m, ct) => { var g = await probe.ProbeAnthropicAsync(m, ct); return (g.Compatibility, $"{g.Health}; {g.Compatibility}"); });
        return new World
        {
            Exec = exec, Claude = claude, Codex = codex, Runtime = runtime, Catalogue = catalogue, Install = new AgentToolInstallationService(catalogue, queue, audit),
            Stacks = stacks, Verify = verify, Templates = new TemplateEngineParts(templates, builder), Matrix = matrix,
        };
    }

    [Fact]
    public async Task Live_start_ollama_with_its_own_app()
    {
        if (!Enabled) return;
        var w = Build();
        var before = await w.Runtime.GetStatusAsync(CancellationToken.None);
        output.WriteLine($"before: {before.Health} — {before.Summary}");
        var r = await new OllamaLauncher(w.Exec, new OllamaDetector(), w.Runtime).EnsureRunningAsync(CancellationToken.None);
        output.WriteLine($"launcher: {r.Success} — {r.Message} {r.Value?.Summary}");
        // The app's executor goes away (as when the app closes); Ollama must keep running.
        w.Exec.Dispose();
        await Task.Delay(2000);
        var after = await w.Runtime.GetStatusAsync(CancellationToken.None);
        output.WriteLine($"after the executor was disposed: {after.Health} — {after.Summary}");
        Assert.True(r.Success, r.Message);
        Assert.True(after.Health is DarkCipher.AgentControl.Domain.Common.ComponentHealth.Healthy or DarkCipher.AgentControl.Domain.Common.ComponentHealth.Degraded, after.Summary);
    }

    [Fact]
    public async Task Live_install_tools_and_verify_stacks()
    {
        if (!Enabled) return;
        var w = Build();
        using var _ = w.Exec;
        var tools = (Environment.GetEnvironmentVariable("DCAC_LIVE_TOOLS") ?? "codex,claude-code").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 1. Install through the real path (approval given by the person running this test).
        foreach (var id in tools)
        {
            var status = await w.Catalogue.Find(id)!.DetectAsync(CancellationToken.None);
            if (status.Installed) { output.WriteLine($"{id}: already installed ({status.Version}); not reinstalled."); continue; }
            var plan = await w.Install.PlanAsync(id, InstallOperation.Install, CancellationToken.None);
            Assert.True(plan.Success, plan.Message);
            output.WriteLine($"PLAN {id}:");
            foreach (var d in plan.Value!.ApprovalDetails()) output.WriteLine("  " + d);
            var approval = new ScriptedApproval(true);
            var job = await w.Install.SubmitAsync(plan.Value, approval, CancellationToken.None);
            Assert.True(job.Success, job.Message);
            await Wait.UntilAsync(() => job.Value!.IsFinished, (int)TimeSpan.FromMinutes(20).TotalMilliseconds);
            foreach (var l in job.Value!.Log) output.WriteLine("  " + l);
            output.WriteLine($"RESULT {id}: {job.Value.StageLabel} — {job.Value.VerificationResult} {job.Value.Error}");
            Assert.True(job.Value.Phase is DownloadPhase.Ready or DownloadPhase.ReadyWithLimitations, job.Value.Error ?? job.Value.Status);
        }

        // 2. Build stacks from templates (deterministic configuration) and run the full verification.
        var cases = new List<(string Template, TemplateSelection Selection)>();
        if (tools.Contains("codex")) cases.Add(("code-review-local", new TemplateSelection { AgentId = "codex", Model = Model, ContextLength = 32768 }));
        if (tools.Contains("claude-code")) cases.Add(("claude-local-gateway", new TemplateSelection { AgentId = "claude-code", AgentMode = AgentMode.ExperimentalLocalGateway, Model = Model, ContextLength = 65536 }));
        foreach (var (template, selection) in cases)
        {
            var t = w.Templates.Catalogue.Find(template)!;
            var installs = new Dictionary<string, string>();
            foreach (var a in new IAgentProvider[] { w.Claude, w.Codex })
            {
                a.InvalidateDetection();
                var d = await a.DetectAsync(CancellationToken.None);
                if (d.Version is not null) installs[a.Id] = Domain.Common.VersionText.Parse(d.Version)?.ToString(3) ?? d.Version;
            }
            var shown = await w.Runtime.ShowAsync(Model, CancellationToken.None);
            var gen = w.Templates.Builder.Generate(new ConfigurationInputs(t, selection, installs, shown?.Digest, shown?.Capabilities), BuiltInPermissionProfiles.Get(t.PermissionProfileId));
            foreach (var warn in gen.Warnings) output.WriteLine("  warning: " + warn);
            Assert.True(w.Stacks.Save(gen.Stack).Success);
            var progress = new Progress<string>(s => output.WriteLine("  … " + s));
            var r = await w.Verify.VerifyAsync(gen.Stack.Id, new ScriptedApproval(false), progress, CancellationToken.None);
            output.WriteLine($"VERIFY {gen.Stack.Name}: {r.Label}");
            foreach (var s in r.Steps) output.WriteLine($"  {(s.Ok ? "PASS" : "FAIL")} {s.Name}: {s.Detail}");
        }

        var md = w.Matrix.ToMarkdown();
        output.WriteLine(md);
        await File.WriteAllTextAsync(Path.Combine(TempDir.DevScratch(), "live-matrix.md"), md);
        Assert.NotEmpty(w.Matrix.Entries);
    }
}
