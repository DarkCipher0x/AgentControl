using DarkCipher.AgentControl.Application.Setup;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Runtimes;

namespace DarkCipher.AgentControl.Tests.Application;

/// <summary>Quick Setup automates installs through the catalogue (found in use: it used to stop at "install Codex yourself").</summary>
public class SetupPlannerTests
{
    private static readonly RuntimeStatus Running = new("ollama", ComponentHealth.Healthy, RuntimeConnection.Local, "0.40.6", null, @"C:\o", "Ollama 0.40.6");
    private static readonly RuntimeStatus Stopped = new("ollama", ComponentHealth.Unavailable, RuntimeConnection.Unavailable, null, null, @"C:\o", "not running");
    private static readonly RuntimeStatus Missing = new("ollama", ComponentHealth.Unavailable, RuntimeConnection.Unavailable, null, null, null, "not installed");
    private static AgentInstallation Agent(string id, bool installed) =>
        new(id, installed, installed ? @"C:\a\" + id + ".exe" : null, installed ? "1.0" : null, installed ? ComponentHealth.Healthy : ComponentHealth.Unavailable, installed ? "ok" : "missing", []);

    private static SetupPlan Build(RuntimeStatus rt, bool agentInstalled, string agent = "codex", bool automatic = true, OneClickPlan plan = OneClickPlan.Balanced) =>
        SetupPlanner.Build(plan, Hw.Profile(8L << 30, 64L << 30), rt, [Agent(agent, agentInstalled)], null, agent, null, @"C:\p", @"C:\t", automatic);

    [Fact]
    public void Missing_agent_and_runtime_are_installed_automatically_through_the_catalogue()
    {
        var p = Build(Missing, agentInstalled: false);
        Assert.DoesNotContain(p.Operations, o => o.State == PlannedOperationState.ManualActionRequired);
        var rt = Assert.Single(p.Operations, o => o.Kind == PlannedOperationKind.InstallRuntime);
        Assert.Equal(("ollama", PlannedOperationState.Pending, true), (rt.ToolId, rt.State, rt.RequiresApproval));
        var ag = Assert.Single(p.Operations, o => o.Kind == PlannedOperationKind.InstallAgent);
        Assert.Equal(("codex", PlannedOperationState.Pending, true, false), (ag.ToolId, ag.State, ag.RequiresApproval, ag.RequiresElevation));
        Assert.Contains("OpenAI.Codex", ag.Detail, StringComparison.Ordinal);
        Assert.Equal("Anthropic.ClaudeCode", SetupPlanner.WingetId("claude-code"));
    }

    [Fact]
    public void A_stopped_runtime_is_started_and_installed_components_are_skipped()
    {
        var p = Build(Stopped, agentInstalled: true);
        var start = Assert.Single(p.Operations, o => o.Kind == PlannedOperationKind.StartRuntime);
        Assert.Equal(PlannedOperationState.Pending, start.State);
        Assert.Equal(PlannedOperationState.Skipped, Assert.Single(p.Operations, o => o.Kind == PlannedOperationKind.InstallAgent).State);

        var ready = Build(Running, agentInstalled: true);
        Assert.All(ready.Operations.Where(o => o.Kind is PlannedOperationKind.StartRuntime or PlannedOperationKind.InstallAgent), o => Assert.Equal(PlannedOperationState.Skipped, o.State));
    }

    [Fact]
    public void Without_winget_the_plan_falls_back_to_the_official_manual_route()
    {
        var p = Build(Missing, agentInstalled: false, agent: "claude-code", automatic: false);
        Assert.Equal(2, p.Operations.Count(o => o.State == PlannedOperationState.ManualActionRequired && o.Source!.StartsWith("https://", StringComparison.Ordinal)));
    }

    [Fact]
    public void Hybrid_does_not_install_or_start_Ollama()
    {
        var p = Build(Missing, agentInstalled: true, plan: OneClickPlan.Hybrid);
        Assert.DoesNotContain(p.Operations, o => o.Kind is PlannedOperationKind.InstallRuntime or PlannedOperationKind.StartRuntime);
        Assert.Contains(p.Warnings, w => w.Contains("Hybrid does not need it", StringComparison.Ordinal));
    }
}
