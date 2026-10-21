using System.Net;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Tools;

namespace DarkCipher.AgentControl.Tests.Tools;

/// <summary>The reviewed catalogue beyond Claude Code, Codex and Ollama: runtimes, gateways, agent CLIs, supporting tools.</summary>
public class CatalogueExpansionTests
{
    private const string Table = """
        Name                          Id                        Version   Available
        ----------------------------------------------------------------------------
        LM Studio                     ElementLabs.LMStudio      0.4.26+4
        ripgrep                       BurntSushi.ripgrep.MSVC   15.2.0
        """;

    private static (WingetClient Winget, ScriptedExecutor Exec) Winget(string table = Table)
    {
        var exec = new ScriptedExecutor(s => s.Arguments.FirstOrDefault() == "list" ? ScriptedExecutor.Ok(table) : ScriptedExecutor.Ok(""));
        return (new WingetClient(exec, () => @"C:\fake\winget.exe"), exec);
    }

    [Fact]
    public void Every_entry_is_pinned_reviewed_and_unique()
    {
        var entries = BuiltInCatalogue.Entries;
        Assert.True(entries.Count >= 17);
        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        Assert.Equal(entries.Count, entries.Select(e => e.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var e in entries)
        {
            Assert.True(WingetClient.IsValidPackageId(e.PackageId), e.PackageId);
            Assert.StartsWith("https://", e.Homepage, StringComparison.Ordinal);
            Assert.NotEmpty(e.Commands);
            Assert.All(e.Commands, c => Assert.EndsWith(".exe", c, StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(e.Licence));
        }
        // Packages without a per-user installer were left out on purpose (the app never asks for administrator rights).
        Assert.DoesNotContain(entries, e => e.PackageId is "nomic.gpt4all" or "Docker.DockerDesktop");
        // The three first-class tools keep their own providers.
        Assert.DoesNotContain(entries, e => e.PackageId is "Anthropic.ClaudeCode" or "OpenAI.Codex" or "Ollama.Ollama");
        Assert.All(BuiltInCatalogue.Gateways, g => Assert.True(g.Parent == "ollama" || entries.Any(e => e.Id == g.Parent), g.Parent));
    }

    [Fact]
    public async Task One_winget_snapshot_serves_every_entry_and_detects_versions()
    {
        var (winget, exec) = Winget();
        var providers = BuiltInCatalogue.Entries.Select(e => new CatalogueWingetToolProvider(winget, exec, e)).ToList();
        var statuses = await Task.WhenAll(providers.Select(p => p.DetectAsync(CancellationToken.None)));
        Assert.Equal(1, exec.Runs.Count(r => r.Arguments.FirstOrDefault() == "list"));
        var lm = statuses.Single(s => s.ToolId == "lmstudio");
        Assert.Equal((ToolInstallState.Installed, "0.4.26+4"), (lm.State, lm.Version));
        Assert.Equal("15.2.0", statuses.Single(s => s.ToolId == "ripgrep").Version);
    }

    [Fact]
    public async Task Installs_invalidate_the_snapshot_and_plans_are_per_user_exact_id()
    {
        var (winget, exec) = Winget(table: "Name  Id  Version\n");
        var entry = BuiltInCatalogue.Entries.Single(e => e.Id == "opencode");
        var p = new CatalogueWingetToolProvider(winget, exec, entry);
        var plan = await p.PlanAsync(InstallOperation.Install, CancellationToken.None);
        Assert.Contains("--scope user", plan.CommandPreview, StringComparison.Ordinal);
        Assert.Contains("--exact", plan.CommandPreview, StringComparison.Ordinal);
        Assert.Contains("SST.opencode", plan.CommandPreview, StringComparison.Ordinal);
        Assert.True(p.Descriptor.CatalogueOnly);
        Assert.Contains(p.Descriptor.KnownLimitations, l => l.StartsWith("Catalogue only", StringComparison.Ordinal));
        var lists = exec.Runs.Count(r => r.Arguments.FirstOrDefault() == "list");
        await p.ExecuteAsync(plan, new Progress<Domain.Downloads.DownloadProgress>(), CancellationToken.None);
        Assert.True(exec.Runs.Count(r => r.Arguments.FirstOrDefault() == "list") > lists, "the snapshot is read again after an install");
    }

    [Fact]
    public async Task Tools_installed_outside_winget_are_found_by_their_executable()
    {
        using var t = new TempDir();
        var exe = Path.Combine(t.Path, "rg.exe");
        File.WriteAllText(exe, "");
        var (winget, _) = Winget(table: "");
        var exec = new ScriptedExecutor(_ => ScriptedExecutor.Ok("ripgrep 14.1.0"));
        var entry = BuiltInCatalogue.Entries.Single(e => e.Id == "ripgrep") with { KnownPaths = [exe] };
        var s = await new CatalogueWingetToolProvider(winget, exec, entry).DetectAsync(CancellationToken.None);
        Assert.Equal((ToolInstallState.Installed, "14.1.0", exe), (s.State, s.Version, s.ExecutablePath));
        Assert.Contains("outside winget", s.Summary, StringComparison.Ordinal);

        // GUI apps are never started just to read a version.
        var gui = BuiltInCatalogue.Entries.Single(e => e.Id == "lmstudio") with { KnownPaths = [exe] };
        var count = exec.Runs.Count;
        var g = await new CatalogueWingetToolProvider(winget, exec, gui).DetectAsync(CancellationToken.None);
        Assert.True(g.Installed);
        Assert.Equal(count, exec.Runs.Count);
    }

    [Fact]
    public async Task Gateways_install_their_runtime_and_report_their_port()
    {
        var (winget, exec) = Winget();
        var lmstudio = new CatalogueWingetToolProvider(winget, exec, BuiltInCatalogue.Entries.Single(e => e.Id == "lmstudio"));
        var g = new RuntimeGatewayToolProvider(lmstudio, "lmstudio-server", "LM Studio local server", "d", 1234, ["OpenAI Chat Completions"], "Start it.");
        Assert.Equal(("lmstudio", ToolKind.Gateway, true, false, false), (g.Descriptor.PartOfToolId, g.Descriptor.Kind, g.Descriptor.CanInstall, g.Descriptor.CanUpdate, g.Descriptor.CanUninstall));
        await Assert.ThrowsAsync<InvalidOperationException>(() => g.PlanAsync(InstallOperation.Update, CancellationToken.None));

        var previous = LoopbackPorts.Listeners;
        try
        {
            LoopbackPorts.Listeners = () => [];
            var stopped = await g.DetectAsync(CancellationToken.None);
            Assert.Equal((ToolInstallState.Installed, ComponentHealth.Unavailable), (stopped.State, stopped.Health));
            LoopbackPorts.Listeners = () => [new IPEndPoint(IPAddress.Loopback, 1234)];
            Assert.Equal(ComponentHealth.Healthy, (await g.DetectAsync(CancellationToken.None)).Health);
        }
        finally { LoopbackPorts.Listeners = previous; }

        var (empty, exec2) = Winget(table: "");
        var jan = new CatalogueWingetToolProvider(empty, exec2, BuiltInCatalogue.Entries.Single(e => e.Id == "jan") with { KnownPaths = [] });
        var janServer = new RuntimeGatewayToolProvider(jan, "jan-server", "Jan local API server", "d", 1337, ["OpenAI Chat Completions"], "Enable it.");
        var plan = await janServer.PlanAsync(InstallOperation.Install, CancellationToken.None);
        Assert.Equal("jan", plan.ToolId);
        Assert.Contains(plan.Warnings, w => w.Contains("installs Jan", StringComparison.Ordinal));
    }

    [Fact]
    public void The_built_in_set_binds_every_gateway_to_its_runtime()
    {
        var (winget, exec) = Winget();
        var ollama = new OllamaToolProvider(winget, new DarkCipher.AgentControl.Integrations.Ollama.OllamaDetector(), new FakeRuntime());
        var set = new BuiltInCatalogueSet(winget, exec, ollama);
        Assert.Equal(BuiltInCatalogueSet.Count, set.All.Count);
        Assert.All(set.All.Where(p => p.Descriptor.Kind == ToolKind.Gateway), p => Assert.NotNull(p.Descriptor.PartOfToolId));
        Assert.Contains(set.All, p => p.Descriptor.Id == "ollama-openai-api" && p.Descriptor.PartOfToolId == "ollama");
    }
}
