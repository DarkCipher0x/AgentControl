using DarkCipher.AgentControl.Application.Setup;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Integrations.Agents;
using DarkCipher.AgentControl.Security.Paths;
using DarkCipher.AgentControl.Security.Processes;
using DarkCipher.AgentControl.Security.Redaction;
using Xunit.Abstractions;

namespace DarkCipher.AgentControl.Tests.Integrations;

/// <summary>
/// Opt-in end-to-end launch of the REAL Claude Code binary through the real secure process executor, using the plan the
/// app builds (local gateway to Ollama on loopback), inside a disposable project under the developer scratch folder (see TempDir).
/// Runs headless (captured, no window): Claude Code's documented "-p" print mode is appended to the app's plan.
/// Enable with DCAC_LIVE_AGENT=1 (and optionally DCAC_LIVE_MODEL). Without it, the tests return immediately.
/// Side effect to know about: Claude Code records the folder in its own user config (~/.claude.json).
/// </summary>
public class LiveAgentTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private static bool Enabled => Environment.GetEnvironmentVariable("DCAC_LIVE_AGENT") == "1";
    private static string Model => Environment.GetEnvironmentVariable("DCAC_LIVE_MODEL") ?? "gpt-oss-64k";

    private (SecureProcessExecutionService Exec, ClaudeCodeProvider Claude, MemoryAudit Audit, string Project) Setup()
    {
        var audit = new MemoryAudit();
        var paths = new TestPaths(_t.Sub("data"));
        var pps = new ProtectedPathService(ProtectedPathService.DefaultBuiltIns(paths), null, audit);
        var exec = new SecureProcessExecutionService(pps, new Redactor(), audit, null, new MemoryLog());
        var project = TestProjectFactory.Create(paths.TestProjectsDirectory);
        return (exec, new ClaudeCodeProvider(exec), audit, project);
    }

    private static AgentLaunchPlan Plan(ClaudeCodeProvider claude, AgentInstallation install, string project) => claude.BuildLaunchPlan(new AgentLaunchRequest
    {
        Installation = install, Mode = AgentMode.ExperimentalLocalGateway, ProjectDirectory = project,
        Permissions = BuiltInPermissionProfiles.Get(BuiltInPermissionProfiles.AskBeforeChangesId), Model = Model, ContextLength = 65536,
    });

    [Fact]
    public async Task Live_claude_code_answers_through_the_local_gateway_headless()
    {
        if (!Enabled) return;
        var (exec, claude, audit, project) = Setup();
        using var _ = exec;
        var install = await claude.DetectAsync(CancellationToken.None);
        Assert.True(install.Installed, install.Summary);
        var plan = Plan(claude, install, project);
        var spec = plan.Process with
        {
            Mode = ProcessMode.Captured,
            Arguments = [.. plan.Process.Arguments, "-p", "Reply with exactly the word READY and nothing else."],
            Timeout = TimeSpan.FromMinutes(5),
        };
        output.WriteLine("Command: " + exec.Preview(spec));
        var r = await exec.RunAsync(spec, CancellationToken.None);
        output.WriteLine($"{r.Outcome} ({r.Message}) exit {r.ExitCode} in {r.Duration.TotalSeconds:0.0}s; stdout: {r.StdOut.Trim()}; stderr: {r.StdErr.Trim()}");
        Assert.True(r.Succeeded, r.Message + " " + r.StdErr);
        Assert.Contains("READY", r.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(audit.Events, e => e.Action == "ProcessStarted");
    }

    [Fact]
    public async Task Live_claude_code_session_can_be_started_and_stopped_as_a_tree()
    {
        if (!Enabled) return;
        var (exec, claude, _, project) = Setup();
        using var __ = exec;
        var install = await claude.DetectAsync(CancellationToken.None);
        Assert.True(install.Installed, install.Summary);
        var plan = Plan(claude, install, project);
        // Captured mode = no console window on the desktop; same executor path (job object, duplicate key, audit) as sessions.
        var spec = plan.Process with
        {
            Mode = ProcessMode.Captured,
            Arguments = [.. plan.Process.Arguments, "-p", "Count from 1 to 400, one number per line."],
            DuplicateKey = "live-session-test",
        };
        var started = exec.Start(spec);
        Assert.True(started.Success, started.Message);
        Assert.False(exec.Start(spec).Success); // duplicate refused while running
        var exited = new TaskCompletionSource<TrackedProcess>();
        exec.ProcessExited += (_, p) => { if (p.Id == started.Process!.Id) exited.TrySetResult(p); };
        await Task.Delay(1500);
        Assert.True(exec.Stop(started.Process!.Id));
        var done = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(exited.Task, done);
        output.WriteLine($"Stopped pid {started.Process.ProcessId}; exit code {exited.Task.Result.ExitCode}");
        Assert.True(exec.Start(spec).Success); // key released after exit
    }
}
