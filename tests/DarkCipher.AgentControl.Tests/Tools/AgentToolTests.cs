using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Agents;
using DarkCipher.AgentControl.Integrations.Tools;

namespace DarkCipher.AgentControl.Tests.Tools;

public class WingetClientTests
{
    [Fact]
    public void Install_arguments_pin_the_exact_id_and_source_and_never_use_a_shell()
    {
        var args = WingetClient.InstallArguments("Anthropic.ClaudeCode", null);
        Assert.Equal(["install", "--id", "Anthropic.ClaudeCode", "--exact", "--source", "winget"], args.Take(6));
        Assert.Contains("--accept-package-agreements", args);
        Assert.Contains("--disable-interactivity", args);
        Assert.Contains("user", args);
        Assert.DoesNotContain(args, a => a.Contains('&') || a.Contains('|') || a.Contains(';'));
    }

    [Theory]
    [InlineData("Anthropic.ClaudeCode", true)]
    [InlineData("OpenAI.Codex", true)]
    [InlineData("Ollama.Ollama", true)]
    [InlineData("evil; rm -rf", false)]
    [InlineData("a..b", false)]
    [InlineData("--source=other", false)]
    [InlineData("", false)]
    public void Package_ids_are_validated(string id, bool ok) => Assert.Equal(ok, WingetClient.IsValidPackageId(id));

    [Fact]
    public void Show_and_list_output_are_parsed_strictly()
    {
        Assert.Equal("2.1.302", WingetClient.ParseShowVersion("Found Claude Code [Anthropic.ClaudeCode]\nVersion: 2.1.302\nPublisher: Anthropic\n"));
        Assert.Null(WingetClient.ParseShowVersion("Version: $(evil)"));
        var list = "Name        Id                   Version  Available  Source\n-------------------------------------------------\nCodex CLI   OpenAI.Codex         0.160.0  0.161.0    winget\n";
        Assert.Equal("0.160.0", WingetClient.ParseListVersion(list, "OpenAI.Codex"));
        Assert.Null(WingetClient.ParseListVersion(list, "Other.Package"));
    }

    [Fact]
    public void Known_winget_failures_are_explained()
    {
        var hash = new ProcessResult(ProcessOutcome.Completed, unchecked((int)0x8A150011), "", "", TimeSpan.Zero, "");
        Assert.Contains("hash did not match", WingetClient.Explain(hash), StringComparison.Ordinal);
        Assert.Contains("0x12345678", WingetClient.Explain(new ProcessResult(ProcessOutcome.Completed, 0x12345678, "", "", TimeSpan.Zero, "")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Missing_winget_is_reported_not_executed()
    {
        var exec = new ScriptedExecutor(_ => ScriptedExecutor.Ok());
        var client = new WingetClient(exec, () => null);
        var r = await client.RunAsync(["--version"], TimeSpan.FromSeconds(5), "test", CancellationToken.None);
        Assert.Equal(ProcessOutcome.StartFailed, r.Outcome);
        Assert.Empty(exec.Runs);
    }
}

public class WingetToolProviderTests
{
    private static (ClaudeCodeToolProvider Tool, ScriptedExecutor Exec) Claude(Func<ProcessSpec, ProcessResult> script)
    {
        var exec = new ScriptedExecutor(script);
        var winget = new WingetClient(exec, () => @"C:\winget\winget.exe");
        return (new ClaudeCodeToolProvider(winget, new ClaudeCodeProvider(exec)), exec);
    }

    [Fact]
    public async Task Install_plan_lists_everything_the_specification_requires()
    {
        var (tool, _) = Claude(s => s.Arguments.Contains("show") ? ScriptedExecutor.Ok("Version: 2.1.302\n") : ScriptedExecutor.Exit(1));
        var plan = await tool.PlanAsync(InstallOperation.Install, CancellationToken.None);
        Assert.Equal("Claude Code", plan.DisplayName);
        Assert.Equal("Anthropic", plan.Publisher);
        Assert.Contains("Anthropic.ClaudeCode", plan.Source, StringComparison.Ordinal);
        Assert.Equal("2.1.302", plan.Version);
        Assert.Contains("no administrator", plan.Privileges, StringComparison.Ordinal);
        Assert.NotEmpty(plan.Downloads);
        Assert.NotEmpty(plan.AffectedLocations);
        Assert.Contains(plan.EnvironmentChanges, e => e.Contains("User-level PATH", StringComparison.Ordinal));
        Assert.Contains("winget", plan.CommandPreview, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(plan.Licence));
        Assert.False(string.IsNullOrWhiteSpace(plan.Rollback));
        Assert.True(plan.EstimatedDiskBytes > 0);
        Assert.Contains(plan.ApprovalDetails(), d => d.StartsWith("Rollback:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plans_are_side_effect_free_only_lookups_run()
    {
        var (tool, exec) = Claude(s => s.Arguments.Contains("show") ? ScriptedExecutor.Ok("Version: 2.1.302\n") : ScriptedExecutor.Exit(1));
        await tool.PlanAsync(InstallOperation.Install, CancellationToken.None);
        Assert.DoesNotContain(exec.Runs, r => r.Arguments.Contains("install") || r.Arguments.Contains("uninstall"));
    }

    [Fact]
    public void Fingerprint_changes_when_what_runs_changes()
    {
        var a = new InstallPlan { ToolId = "t", Operation = InstallOperation.Install, DisplayName = "T", Publisher = "P", Source = "s", Version = "1.0.0", Method = "m", Privileges = "p", Authentication = "a", Licence = "l", Rollback = "r", CommandPreview = "winget install" };
        Assert.Equal(a.Fingerprint, (a with { Warnings = ["reworded"] }).Fingerprint);
        Assert.NotEqual(a.Fingerprint, (a with { Version = "1.0.1" }).Fingerprint);
        Assert.NotEqual(a.Fingerprint, (a with { CommandPreview = "winget install --force" }).Fingerprint);
        Assert.NotEqual(a.Fingerprint, (a with { EnvironmentChanges = ["PATH"] }).Fingerprint);
    }

    [Fact]
    public void Repair_is_never_offered_for_a_healthy_tool()
    {
        var healthy = new ToolStatus("x", ToolInstallState.Installed, "1.0.0", @"C:\x.exe", ToolAuthState.Unknown, ComponentHealth.Healthy, "ok");
        Assert.Null(WingetToolProvider.Diagnose(healthy));
        Assert.NotNull(WingetToolProvider.Diagnose(healthy with { Health = ComponentHealth.Degraded, Summary = "broken" }));
        Assert.NotNull(WingetToolProvider.Diagnose(healthy with { Version = null }));
    }

    [Fact]
    public async Task Gateway_cannot_be_installed_separately()
    {
        var gw = new OllamaGatewayToolProvider(new FakeRuntime());
        Assert.False(gw.Descriptor.CanInstall);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gw.PlanAsync(InstallOperation.Install, CancellationToken.None));
        var s = await gw.DetectAsync(CancellationToken.None);
        Assert.Equal(ComponentHealth.Unknown, s.Health); // not proven until the gateway test passes
    }

    [Fact]
    public void Extension_catalogue_entries_are_validated_and_always_unverified()
    {
        var winget = new WingetClient(new ScriptedExecutor(_ => ScriptedExecutor.Ok()), () => null);
        Assert.Throws<ArgumentException>(() => new ExtensionWingetToolProvider(winget, "ext", "x", "X", "P", "", "bad id; calc", "https://example.org", "MIT", []));
        Assert.Throws<ArgumentException>(() => new ExtensionWingetToolProvider(winget, "ext", "x", "X", "P", "", "Good.Id", "http://example.org", "MIT", []));
        var ok = new ExtensionWingetToolProvider(winget, "ext", "x", "X", "P", "", "Good.Id", "https://example.org", "MIT", []);
        Assert.Equal(ToolTrust.Unverified, ok.Descriptor.Trust);
        Assert.StartsWith("ext-", ok.Descriptor.Id, StringComparison.Ordinal);
    }
}

public class AgentToolInstallationTests
{
    private static (AgentToolCatalogueService Catalogue, AgentToolInstallationService Install, DownloadManager Queue, FakeToolProvider Tool, MemoryAudit Audit) Make()
    {
        var tool = new FakeToolProvider();
        var audit = new MemoryAudit();
        var catalogue = new AgentToolCatalogueService([tool], new CustomAgentRegistry(new MemoryStore(), audit), _ => throw new InvalidOperationException());
        var queue = new DownloadManager([new AgentToolQueueProvider(catalogue, audit)], new MemoryLog(), audit, () => null, [new ToolInstallVerifier(catalogue)]);
        return (catalogue, new AgentToolInstallationService(catalogue, queue, audit), queue, tool, audit);
    }

    [Fact]
    public async Task Declined_approval_installs_nothing()
    {
        var (_, install, queue, tool, audit) = Make();
        var plan = (await install.PlanAsync("fake-tool", InstallOperation.Install, CancellationToken.None)).Value!;
        var approval = new ScriptedApproval(false);
        var r = await install.SubmitAsync(plan, approval, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Single(approval.Requests);
        Assert.Contains(approval.Requests[0].Details, d => d.StartsWith("Source:", StringComparison.Ordinal));
        Assert.Empty(queue.Jobs);
        Assert.Equal(0, tool.Executions);
        Assert.Contains(audit.Events, e => e.Action == "ToolOperationDeclined");
    }

    [Fact]
    public async Task Approved_install_runs_tests_and_becomes_ready_only_when_detected_in_the_verified_range()
    {
        var (_, install, queue, tool, _) = Make();
        tool.AfterExecute = new ToolStatus("fake-tool", ToolInstallState.Installed, "1.2.0", @"C:\t.exe", ToolAuthState.NotRequired, ComponentHealth.Healthy, "ok");
        var plan = (await install.PlanAsync("fake-tool", InstallOperation.Install, CancellationToken.None)).Value!;
        var r = await install.SubmitAsync(plan, new ScriptedApproval(true), CancellationToken.None);
        Assert.True(r.Success, r.Message);
        await Wait.UntilAsync(() => r.Value!.IsFinished);
        Assert.Equal(DownloadPhase.Ready, r.Value!.Phase);
        Assert.Equal(1, tool.Executions);
        Assert.Contains("verified range", r.Value.VerificationResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newer_than_verified_version_is_ready_with_limitations()
    {
        var (_, install, _, tool, _) = Make();
        tool.AfterExecute = new ToolStatus("fake-tool", ToolInstallState.Installed, "9.0.0", @"C:\t.exe", ToolAuthState.NotRequired, ComponentHealth.Healthy, "ok");
        var plan = (await install.PlanAsync("fake-tool", InstallOperation.Install, CancellationToken.None)).Value!;
        var r = await install.SubmitAsync(plan, new ScriptedApproval(true), CancellationToken.None);
        await Wait.UntilAsync(() => r.Value!.IsFinished);
        Assert.Equal(DownloadPhase.ReadyWithLimitations, r.Value!.Phase);
    }

    [Fact]
    public async Task Install_that_is_not_detected_afterwards_requires_rollback()
    {
        var (_, install, _, tool, _) = Make();
        var plan = (await install.PlanAsync("fake-tool", InstallOperation.Install, CancellationToken.None)).Value!;
        var r = await install.SubmitAsync(plan, new ScriptedApproval(true), CancellationToken.None);
        await Wait.UntilAsync(() => r.Value!.IsFinished);
        Assert.Equal(DownloadPhase.RollbackRequired, r.Value!.Phase);
    }

    [Fact]
    public async Task Failed_installer_is_reported_as_failed()
    {
        var (_, install, _, tool, _) = Make();
        tool.ExecuteSucceeds = false;
        var plan = (await install.PlanAsync("fake-tool", InstallOperation.Install, CancellationToken.None)).Value!;
        var r = await install.SubmitAsync(plan, new ScriptedApproval(true), CancellationToken.None);
        await Wait.UntilAsync(() => r.Value!.IsFinished);
        Assert.Equal(DownloadPhase.Failed, r.Value!.Phase);
        Assert.Contains("winget failed", r.Value.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plan_that_changed_after_approval_is_not_run()
    {
        var (_, install, _, tool, audit) = Make();
        var plan = (await install.PlanAsync("fake-tool", InstallOperation.Install, CancellationToken.None)).Value!;
        tool.Available = "1.3.0";                            // a new version is published between approval and execution
        var r = await install.SubmitAsync(plan, new ScriptedApproval(true), CancellationToken.None);
        await Wait.UntilAsync(() => r.Value!.IsFinished);
        Assert.Equal(DownloadPhase.Failed, r.Value!.Phase);
        Assert.Equal(0, tool.Executions);
        Assert.Contains(audit.Events, e => e.Action == "ToolPlanChanged");
    }

    [Fact]
    public async Task Unmet_prerequisites_block_submission()
    {
        var (_, install, queue, tool, _) = Make();
        tool.Status = new ToolStatus("fake-tool", ToolInstallState.Installed, "1.0.0", null, ToolAuthState.NotRequired, ComponentHealth.Healthy, "ok");
        var plan = (await install.PlanAsync("fake-tool", InstallOperation.Install, CancellationToken.None)).Value!;
        var approval = new ScriptedApproval(true);
        var r = await install.SubmitAsync(plan, approval, CancellationToken.None);
        Assert.False(r.Success);
        Assert.Empty(approval.Requests);
        Assert.Empty(queue.Jobs);
    }

    [Fact]
    public async Task Uninstall_is_verified_by_detection()
    {
        var (_, install, _, tool, _) = Make();
        tool.Status = new ToolStatus("fake-tool", ToolInstallState.Installed, "1.2.0", null, ToolAuthState.NotRequired, ComponentHealth.Healthy, "ok");
        tool.AfterExecute = new ToolStatus("fake-tool", ToolInstallState.NotInstalled, null, null, ToolAuthState.NotRequired, ComponentHealth.Unavailable, "gone");
        var plan = (await install.PlanAsync("fake-tool", InstallOperation.Uninstall, CancellationToken.None)).Value!;
        var r = await install.SubmitAsync(plan, new ScriptedApproval(true), CancellationToken.None);
        Assert.True(r.Success, r.Message);
        try { await Wait.UntilAsync(() => r.Value!.IsFinished); }
        catch (TimeoutException) { Assert.Fail($"Stuck in {r.Value!.Phase}: {r.Value.Status} / {string.Join(" | ", r.Value.Log)}"); }
        Assert.Equal(DownloadPhase.Ready, r.Value!.Phase);
        Assert.Equal("Removed", r.Value.Status);
    }

    [Fact]
    public async Task Update_check_and_version_verdicts()
    {
        var (catalogue, _, _, tool, _) = Make();
        tool.Status = new ToolStatus("fake-tool", ToolInstallState.Installed, "1.1.0", null, ToolAuthState.NotRequired, ComponentHealth.Healthy, "ok");
        var updates = await new AgentToolUpdateService(catalogue).CheckAsync(CancellationToken.None);
        Assert.Single(updates);
        Assert.Equal("Within the verified range", updates[0].VersionVerdict);
        tool.Status = tool.Status with { Version = "0.9.0" };
        var e = (await catalogue.RefreshAsync(false, CancellationToken.None))[0];
        Assert.StartsWith("Older than the minimum", e.VersionVerdict, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repair_diagnoses_before_planning()
    {
        var (catalogue, _, _, tool, _) = Make();
        tool.Status = new ToolStatus("fake-tool", ToolInstallState.Installed, "1.2.0", null, ToolAuthState.NotRequired, ComponentHealth.Healthy, "ok");
        var healthy = await new AgentToolRepairService(catalogue).DiagnoseAsync("fake-tool", CancellationToken.None);
        Assert.False(healthy.NeedsRepair);
        Assert.Null(healthy.Plan);
        tool.Status = tool.Status with { State = ToolInstallState.Broken, Health = ComponentHealth.Degraded, Summary = "did not start" };
        var broken = await new AgentToolRepairService(catalogue).DiagnoseAsync("fake-tool", CancellationToken.None);
        Assert.True(broken.NeedsRepair);
        Assert.NotNull(broken.Plan);
        Assert.Contains("did not start", broken.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_a_queued_install_runs_nothing()
    {
        var gate = new TaskCompletionSource();
        var slow = new FakeDownloadProvider("slow") { Behaviour = async (_, _, ct) => { await gate.Task.WaitAsync(ct); return OperationResult.Ok(); } };
        var tool = new FakeToolProvider();
        var audit = new MemoryAudit();
        var catalogue = new AgentToolCatalogueService([tool], new CustomAgentRegistry(new MemoryStore(), audit), _ => throw new InvalidOperationException());
        var queue = new DownloadManager([slow, new AgentToolQueueProvider(catalogue, audit)], new MemoryLog(), audit, () => null);
        var blocker = queue.Enqueue(new DownloadRequest("slow", "blocker", "blocker")).Value!;
        var plan = await tool.PlanAsync(InstallOperation.Install, CancellationToken.None);
        var job = queue.Enqueue(new DownloadRequest(AgentToolQueueProvider.Id_, AgentToolQueueProvider.Target("fake-tool", InstallOperation.Install), "Install") { ApprovedPlanFingerprint = plan.Fingerprint }).Value!;
        queue.Cancel(job.Id);
        gate.SetResult();
        await Wait.UntilAsync(() => blocker.IsFinished && job.IsFinished);
        Assert.Equal(DownloadPhase.Cancelled, job.Phase);
        Assert.Equal(0, tool.Executions);
    }
}

public class CustomAgentTests
{
    [Fact]
    public void Custom_tools_require_a_full_exe_path_and_never_store_secrets()
    {
        var reg = new CustomAgentRegistry(new MemoryStore(), new MemoryAudit());
        Assert.False(reg.Register(new CustomAgentDefinition { Id = "custom-x", DisplayName = "X", Executable = "x.exe" }).Success);
        Assert.False(reg.Register(new CustomAgentDefinition { Id = "custom-x", DisplayName = "X", Executable = @"C:\tools\x.cmd" }).Success);
        Assert.False(reg.Register(new CustomAgentDefinition { Id = "Bad Id", DisplayName = "X", Executable = @"C:\tools\x.exe" }).Success);
        Assert.False(reg.Register(new CustomAgentDefinition { Id = "custom-x", DisplayName = "X", Executable = @"C:\tools\x.exe", Environment = new Dictionary<string, string> { ["OPENAI_KEY"] = "sk-abcdefghijklmnopqrstuvwxyz" } }).Success);
        Assert.False(reg.Register(new CustomAgentDefinition { Id = "custom-x", DisplayName = "X", Executable = @"C:\tools\x.exe", Environment = new Dictionary<string, string> { ["PATH"] = "x" } }).Success);
        var ok = reg.Register(new CustomAgentDefinition { Id = "custom-x", DisplayName = "X", Executable = @"C:\tools\x.exe", Trust = ToolTrust.ValidatedLocally });
        Assert.True(ok.Success);
        Assert.Equal(ToolTrust.Unverified, reg.All.Single().Trust);   // registration never trusts
    }

    [Fact]
    public async Task Validation_runs_the_version_command_through_the_executor()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        var exec = new ScriptedExecutor(_ => ScriptedExecutor.Ok("my-agent 3.4.5\n"));
        var def = new CustomAgentDefinition { Id = "custom-a", DisplayName = "A", Executable = exe };
        var reg = new CustomAgentRegistry(new MemoryStore(), new MemoryAudit());
        reg.Register(def);
        var status = await new CustomAgentToolProvider(def, exec).DetectAsync(CancellationToken.None);
        Assert.Equal("3.4.5", status.Version);
        Assert.Single(exec.Runs);
        Assert.Equal(exe, exec.Runs[0].Executable);
        Assert.True(reg.RecordValidation("custom-a", status).Success);
        Assert.Equal(ToolTrust.ValidatedLocally, reg.All.Single().Trust);
        var failed = await new CustomAgentToolProvider(def, new ScriptedExecutor(_ => ScriptedExecutor.Ok("no version here"))).DetectAsync(CancellationToken.None);
        Assert.False(reg.RecordValidation("custom-a", failed).Success);
        Assert.Equal(ToolTrust.Unverified, reg.All.Single().Trust);
    }

    [Fact]
    public void Catalogue_refuses_contributions_that_shadow_built_in_tools()
    {
        var tool = new FakeToolProvider();
        var catalogue = new AgentToolCatalogueService([tool], new CustomAgentRegistry(new MemoryStore(), new MemoryAudit()), _ => throw new InvalidOperationException());
        var winget = new WingetClient(new ScriptedExecutor(_ => ScriptedExecutor.Ok()), () => null);
        Assert.False(catalogue.RegisterExternal(new ExtensionWingetToolProvider(winget, "ext", "x", "X", "P", "", "Tests.FakeTool", "https://example.org", "MIT", [])).Success);
        Assert.True(catalogue.RegisterExternal(new ExtensionWingetToolProvider(winget, "ext", "y", "Y", "P", "", "Other.Tool", "https://example.org", "MIT", [])).Success);
    }
}
