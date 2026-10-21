using System.IO.Compression;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Sdk;
using DarkCipher.AgentControl.Security.Packages;

namespace DarkCipher.AgentControl.Tests.Extensions;

/// <summary>Loads the compiled samples through the real extension host (collectible AssemblyLoadContext, Development Mode).</summary>
public class CodeExtensionTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly MemoryAudit _audit = new();
    private readonly FixedPolicy _policy = new(new SecurityPolicy { DisableThirdPartyExtensions = false, DevelopmentMode = true });

    public void Dispose() => _t.Dispose();

    private static string SampleBin(string project, string dll)
    {
        var repo = TempDir.RepoRoot;
        foreach (var cfg in new[] { "Debug", "Release" })
        {
            var dir = Path.Combine(repo, "samples", project, "bin", cfg, "net8.0");
            if (File.Exists(Path.Combine(dir, dll))) return dir;
        }
        throw new FileNotFoundException("Build the samples first.", dll);
    }

    private string Pack(string binDir, string dll)
    {
        var src = _t.Sub("src-" + dll);
        File.Copy(Path.Combine(binDir, "extension.json"), Path.Combine(src, "extension.json"));
        File.Copy(Path.Combine(binDir, dll), Path.Combine(src, dll));
        File.WriteAllText(Path.Combine(src, SdkInfo.IntegrityFileName), PackageIntegrity.Build(src, new PackageVerificationService(null)));
        var pkg = Path.Combine(_t.Path, dll + SdkInfo.PackageExtension);
        ZipFile.CreateFromDirectory(src, pkg);
        return pkg;
    }

    private ExtensionHost Host(NoExec? exec = null) => new(new TestPaths(Path.Combine(_t.Path, "data")), new MemoryStore(), new PackageVerificationService(_audit), _policy, _audit,
        new MemoryLog(), new FakeServices(), exec ?? new NoExec(), [], new NoHardware(), new NoCreds());

    [Fact]
    public async Task Dashboard_panel_sample_loads_in_development_mode_and_runs_through_the_broker()
    {
        var host = Host();
        var review = host.Review(Pack(SampleBin("dashboard-panel", "ModelPanel.dll"), "ModelPanel.dll"));
        Assert.True(review.CanInstall, string.Join("; ", review.Errors));
        Assert.Contains("NOT isolated", review.Isolation, StringComparison.Ordinal);
        Assert.True(host.Install(review, review.Manifest.Permissions).Success);
        await host.StartAsync(CancellationToken.None);
        await Task.Delay(200); // Install also triggers a background start

        var rec = host.Extensions.Single(e => e.Id == "com.example.model-panel");
        Assert.Equal(LifecycleState.Ready, rec.State);
        var (_, panel) = Assert.Single(host.Contributions.Panels, p => p.ExtensionId == "com.example.model-panel");
        var (ok, result) = await ExtensionContributionRegistry.InvokeIsolatedAsync(async ct => string.Join(",", (await panel.GetRows(ct)).Select(r => r.Key + "=" + r.Value)), TimeSpan.FromSeconds(5));
        Assert.True(ok, result);
        Assert.Contains("Installed=0", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_detector_sample_runs_its_process_only_through_the_broker()
    {
        var exec = new NoExec();
        var host = Host(exec);
        var review = host.Review(Pack(SampleBin("agent-detector", "AgentDetector.dll"), "AgentDetector.dll"));
        Assert.True(host.Install(review, review.Manifest.Permissions).Success, string.Join("; ", review.Errors));
        await host.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        var (_, cmd) = Assert.Single(host.Contributions.Commands, c => c.ExtensionId == "com.example.agent-detector");
        var (ok, _) = await ExtensionContributionRegistry.InvokeIsolatedAsync(cmd.Execute, TimeSpan.FromSeconds(5));
        Assert.True(ok);
        var spec = Assert.Single(exec.Runs);
        Assert.Equal(PathActor.Extension, spec.Actor);
        Assert.Contains("[extension com.example.agent-detector]", spec.Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_extension_granted_fewer_permissions_is_denied_at_the_broker()
    {
        var host = Host();
        var review = host.Review(Pack(SampleBin("dashboard-panel", "ModelPanel.dll"), "ModelPanel.dll"));
        host.Install(review, []); // user granted nothing
        await host.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        var rec = host.Extensions.Single(e => e.Id == "com.example.model-panel");
        Assert.Equal(LifecycleState.Failed, rec.State); // RegisterDashboardPanel needs UI → CapabilityDeniedException
        Assert.Contains(_audit.Events, e => e.Action == "ExtensionCapabilityDenied");
        Assert.Empty(host.Contributions.Panels);
    }
}
