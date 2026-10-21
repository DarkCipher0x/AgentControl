using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Agents;
using DarkCipher.AgentControl.Integrations.Ollama;
using DarkCipher.AgentControl.Integrations.Tools;
using DarkCipher.AgentControl.Security.Paths;
using DarkCipher.AgentControl.Security.Processes;
using DarkCipher.AgentControl.Security.Redaction;
using Xunit.Abstractions;

namespace DarkCipher.AgentControl.Tests.Integrations;

/// <summary>
/// Opt-in live checks of the catalogue's install path WITHOUT installing anything: winget runs through the real secure
/// process executor (its app-execution alias must pass the executable rules), the reviewed source is queried for the
/// newest version of each built-in package, and every built-in provider plans its install. Enable with DCAC_LIVE_WINGET=1.
/// </summary>
public class LiveToolTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();
    private static bool Enabled => Environment.GetEnvironmentVariable("DCAC_LIVE_WINGET") == "1";

    private SecureProcessExecutionService Exec()
    {
        var audit = new MemoryAudit();
        var pps = new ProtectedPathService(ProtectedPathService.DefaultBuiltIns(new TestPaths(_t.Sub("data"))), null, audit);
        return new SecureProcessExecutionService(pps, new Redactor(), audit, null, new MemoryLog());
    }

    [Fact]
    public async Task Live_winget_runs_through_the_secure_executor()
    {
        if (!Enabled) return;
        using var exec = Exec();
        var winget = new WingetClient(exec);
        Assert.NotNull(winget.ExecutablePath);
        var r = await winget.RunAsync(["--version"], TimeSpan.FromSeconds(60), "Live test: winget version", CancellationToken.None);
        output.WriteLine($"{r.Outcome} exit {r.ExitCode}: {r.StdOut.Trim()} {r.Message}");
        Assert.True(r.Succeeded, r.Message);
        Assert.StartsWith("v", r.StdOut.Trim(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Anthropic.ClaudeCode")]
    [InlineData("OpenAI.Codex")]
    [InlineData("Ollama.Ollama")]
    public async Task Live_winget_source_resolves_each_pinned_package(string id)
    {
        if (!Enabled) return;
        using var exec = Exec();
        var v = await new WingetClient(exec).AvailableVersionAsync(id, CancellationToken.None);
        output.WriteLine($"{id}: {v}");
        Assert.NotNull(v);
    }

    [Fact]
    public async Task Live_built_in_providers_detect_and_plan_without_installing()
    {
        if (!Enabled) return;
        using var exec = Exec();
        var winget = new WingetClient(exec);
        var runtime = new FakeRuntime();
        IAgentToolProvider[] providers =
        [
            new ClaudeCodeToolProvider(winget, new ClaudeCodeProvider(exec)),
            new CodexToolProvider(winget, new CodexProvider(exec)),
            new OllamaToolProvider(winget, new OllamaDetector(), runtime),
        ];
        foreach (var p in providers)
        {
            var s = await p.DetectAsync(CancellationToken.None);
            var plan = await p.PlanAsync(s.Installed ? InstallOperation.Update : InstallOperation.Install, CancellationToken.None);
            output.WriteLine($"{p.Descriptor.DisplayName}: {s.State} {s.Version} → plan {plan.Operation} {plan.Version} [{plan.Fingerprint}] can proceed: {plan.CanProceed}");
            output.WriteLine("  " + plan.CommandPreview);
            Assert.Contains("--exact", plan.CommandPreview, StringComparison.Ordinal);
            Assert.Contains(p.Descriptor.Source!.PackageId, plan.CommandPreview, StringComparison.Ordinal);
        }
    }

    /// <summary>Every reviewed catalogue entry resolves in the live winget source with a per-user installer, and
    /// detection plus planning work against the real winget (nothing is installed).</summary>
    [Fact]
    public async Task Live_catalogue_entries_resolve_per_user_and_plan_without_installing()
    {
        if (!Enabled) return;
        using var exec = Exec();
        var winget = new WingetClient(exec);
        var missing = new List<string>();
        foreach (var e in BuiltInCatalogue.Entries)
        {
            var show = await winget.RunAsync(["show", "--id", e.PackageId, "--exact", "--source", "winget", "--scope", "user", "--accept-source-agreements", "--disable-interactivity"],
                TimeSpan.FromMinutes(2), "Look up " + e.PackageId, CancellationToken.None);
            var perUser = show.Succeeded && show.StdOut.Contains("Installer Type:", StringComparison.Ordinal);
            var p = new CatalogueWingetToolProvider(winget, exec, e);
            var s = await p.DetectAsync(CancellationToken.None);
            var plan = await p.PlanAsync(s.Installed ? InstallOperation.Update : InstallOperation.Install, CancellationToken.None);
            output.WriteLine($"{e.DisplayName,-22} {e.PackageId,-26} per-user installer: {perUser,-5} {s.State,-12} {s.Version,-12} → {plan.Operation} {plan.Version}");
            if (!perUser) missing.Add(e.PackageId);
            if (plan.Operation == InstallOperation.Install) Assert.Contains("--scope user", plan.CommandPreview, StringComparison.Ordinal);
        }
        Assert.Empty(missing);
    }
}
