using DarkCipher.AgentControl.Application.Sessions;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Security.Configuration;
using DarkCipher.AgentControl.Security.Paths;
using DarkCipher.AgentControl.Security.Permissions;

namespace DarkCipher.AgentControl.Tests.Application;

public sealed class RecordingExecutor : IProcessExecutor
{
    public List<ProcessSpec> Started { get; } = [];
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct) => throw new NotSupportedException();
    public OperationResultWithProcess Start(ProcessSpec spec)
    {
        Started.Add(spec);
        return new(true, "Started", new TrackedProcess("t" + Started.Count, 1234, spec.Executable, spec.Purpose, DateTimeOffset.UtcNow, spec.Mode));
    }
    public IReadOnlyList<TrackedProcess> Tracked => [];
    public bool Stop(string trackedId) => true;
    public event EventHandler<TrackedProcess>? ProcessExited { add { } remove { } }
    public string Preview(ProcessSpec spec) => spec.Executable + " " + string.Join(' ', spec.Arguments);
}

public sealed class FakeAgent(string? writePath = null) : IAgentProvider
{
    public string Id => "fake";
    public string DisplayName => "Fake agent";
    public string Vendor => "Tests";
    public FeatureMaturity Maturity => FeatureMaturity.Supported;
    public IReadOnlyList<AgentModeInfo> Modes { get; } = [new(AgentMode.LocalProvider, "Local", FeatureMaturity.Supported, AgentProtocol.OllamaNative, ""), new(AgentMode.Native, "Cloud", FeatureMaturity.Supported, AgentProtocol.ProviderNative, "")];
    public PermissionProfile? LastPermissions { get; private set; }
    public Task<AgentInstallation> DetectAsync(CancellationToken ct) => Task.FromResult(new AgentInstallation(Id, true, @"C:\fake\agent.exe", "1", ComponentHealth.Healthy, "ok", []));
    public void InvalidateDetection() { }
    public AgentLaunchPlan BuildLaunchPlan(AgentLaunchRequest r)
    {
        LastPermissions = r.Permissions;
        var files = writePath is null ? [] : new[] { new PlannedFileChange(writePath, "x", "test") };
        return new AgentLaunchPlan(new ProcessSpec { Executable = @"C:\fake\agent.exe", WorkingDirectory = r.ProjectDirectory, Mode = ProcessMode.InteractiveConsole, Actor = PathActor.Agent },
            r.Mode, "fake backend", FeatureMaturity.Supported, [], files, []);
    }
    public IReadOnlyList<PermissionControl> DescribeEnforcement(PermissionProfile profile, AgentMode mode) => [];
}

public class SessionServiceTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly MemoryAudit _audit = new();
    private readonly RecordingExecutor _exec = new();
    private readonly FixedPolicy _policy = new(new SecurityPolicy());
    private readonly TestPaths _paths;
    private readonly string _protected;
    private readonly string _normal;

    public SessionServiceTests()
    {
        _paths = new TestPaths(_t.Sub("data"));
        _protected = _t.Sub("protected-work");
        _normal = _t.Sub("normal-work");
    }

    public void Dispose() => _t.Dispose();

    private (SessionService Svc, StackService Stacks, ProjectService Projects, FakeAgent Agent) Make(string? agentWrite = null)
    {
        var store = new MemoryStore();
        var pps = new ProtectedPathService([new ProtectedPathEntry(_protected, ProtectedPathRule.ReadOnly, "Protected work", false)], null, _audit);
        var perms = new PermissionService(null, _audit);
        var stacks = new StackService(store, _audit, perms);
        stacks.Save(new StackProfile { Id = "s1", Name = "Local", AgentProviderId = "fake", AgentMode = AgentMode.LocalProvider, PrimaryModel = "m:1", PermissionProfileId = BuiltInPermissionProfiles.StandardId });
        stacks.Save(new StackProfile { Id = "cloud", Name = "Cloud", AgentProviderId = "fake", AgentMode = AgentMode.Native, Privacy = PrivacyMode.CloudAllowed });
        var projects = new ProjectService(store, pps, _audit);
        var agent = new FakeAgent(agentWrite);
        var cfg = new ConfigurationProtectionService(pps, _audit, _paths.BackupDirectory);
        return (new SessionService(stacks, projects, [agent], perms, pps, _exec, cfg, _policy, _audit, _paths, store), stacks, projects, agent);
    }

    [Fact]
    public async Task Normal_project_starts_with_the_requested_profile_and_no_approval()
    {
        var (svc, _, projects, agent) = Make();
        var p = projects.Add(_normal).Value!;
        var approval = new ScriptedApproval(false);
        var r = await svc.StartAsync("s1", p.Id, approval, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Empty(approval.Requests);
        Assert.Equal(BuiltInPermissionProfiles.StandardId, agent.LastPermissions!.Id);
        Assert.False(_exec.Started.Single().PathApprovalGranted);
    }

    [Fact]
    public async Task Read_only_protected_project_forces_read_only_and_needs_approval()
    {
        var (svc, _, projects, agent) = Make();
        var p = projects.Add(_protected).Value!;
        var declined = await svc.StartAsync("s1", p.Id, new ScriptedApproval(false), CancellationToken.None);
        Assert.False(declined.Success);
        Assert.Empty(_exec.Started);

        var approval = new ScriptedApproval(true);
        var r = await svc.StartAsync("s1", p.Id, approval, CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Equal(BuiltInPermissionProfiles.ReadOnlyId, agent.LastPermissions!.Id);
        Assert.True(_exec.Started.Single().PathApprovalGranted);
        Assert.Equal(SecurityClassification.High, approval.Requests.Single().Risk);
        Assert.Contains(_audit.Events, e => e.Action == "ProtectedPathApproved");
    }

    [Fact]
    public async Task Cloud_stacks_are_blocked_in_fully_local_mode()
    {
        var (svc, _, projects, _) = Make();
        var p = projects.Add(_normal).Value!;
        var r = await svc.StartAsync("cloud", p.Id, new ScriptedApproval(true), CancellationToken.None);
        Assert.False(r.Success);
        Assert.Empty(_exec.Started);
    }

    [Fact]
    public async Task Cloud_sessions_need_approval_and_are_audited_when_policy_allows()
    {
        _policy.Current = _policy.Current with { Privacy = PrivacyMode.Hybrid };
        var (svc, _, projects, _) = Make();
        var p = projects.Add(_normal).Value!;
        var approval = new ScriptedApproval(true);
        Assert.True((await svc.StartAsync("cloud", p.Id, approval, CancellationToken.None)).Success);
        Assert.Single(approval.Requests);
        Assert.Contains(_audit.Events, e => e.Action == "CloudInferenceSession");
    }

    [Fact]
    public async Task Plans_that_write_outside_the_agent_profile_folder_are_refused()
    {
        var (svc, _, projects, _) = Make(agentWrite: Path.Combine(_normal, "evil.toml"));
        var p = projects.Add(_normal).Value!;
        var r = await svc.StartAsync("s1", p.Id, new ScriptedApproval(true), CancellationToken.None);
        Assert.False(r.Success);
        Assert.False(File.Exists(Path.Combine(_normal, "evil.toml")));
        Assert.Empty(_exec.Started);
    }

    [Fact]
    public async Task Isolated_profile_files_are_written_inside_the_profile_folder()
    {
        var target = Path.Combine(_paths.AgentProfilesDirectory, "s1", "config.toml");
        var (svc, _, projects, _) = Make(agentWrite: target);
        var p = projects.Add(_normal).Value!;
        Assert.True((await svc.StartAsync("s1", p.Id, new ScriptedApproval(true), CancellationToken.None)).Success);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void Credential_folders_cannot_be_added_as_projects()
    {
        var store = new MemoryStore();
        var cred = _t.Sub(".ssh");
        var pps = new ProtectedPathService([new ProtectedPathEntry(cred, ProtectedPathRule.BlockAllAccess, "Credential store", true, false)], null, _audit);
        var projects = new ProjectService(store, pps, _audit);
        Assert.False(projects.Add(cred).Success);
    }
}
