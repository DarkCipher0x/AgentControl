using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.Tests;

/// <summary>An in-memory model runtime (no Ollama, no network).</summary>
public sealed class FakeRuntime : IModelRuntime
{
    public string Id => "ollama";
    public string DisplayName => "Ollama";
    public FeatureMaturity Maturity => FeatureMaturity.Supported;
    public ComponentHealth Health { get; set; } = ComponentHealth.Healthy;
    public string? Version { get; set; } = "0.40.6";
    public List<ModelDescriptor> Models { get; } = [];

    public static ModelDescriptor Model(string name, ModelCapabilities caps = ModelCapabilities.Completion | ModelCapabilities.Tools, int ctx = 131072, long size = 13_000_000_000) =>
        new() { Name = name, Capabilities = caps, ContextLength = ctx, SizeBytes = size, Digest = "sha256:" + Hashing.Sha256Hex(name)[..12], ParameterSize = "20B", Quantization = "Q4_K_M" };

    public Task<RuntimeStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(new RuntimeStatus(Id, Health,
        Health == ComponentHealth.Healthy ? RuntimeConnection.Local : RuntimeConnection.Unavailable, Health == ComponentHealth.Unavailable ? null : Version,
        null, null, Health == ComponentHealth.Healthy ? "Running" : "Not running"));
    public Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ModelDescriptor>>([.. Models]);
    public Task<IReadOnlyList<RunningModel>> ListRunningAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RunningModel>>([]);
    public Task<ModelDescriptor?> ShowAsync(string model, CancellationToken ct) => Task.FromResult(Models.FirstOrDefault(m => m.Name == model || m.Name == model + ":latest"));
    public Task<OperationResult> PullAsync(string model, IProgress<PullProgress> progress, CancellationToken ct) => Task.FromResult(OperationResult.Ok());
    public Task<OperationResult> DeleteAsync(string model, CancellationToken ct) => Task.FromResult(OperationResult.Ok());
    public Task<OperationResult> CopyAsync(string source, string destination, CancellationToken ct) => Task.FromResult(OperationResult.Ok());
    public Task<OperationResult> UnloadAsync(string model, CancellationToken ct) => Task.FromResult(OperationResult.Ok());
}

public sealed class FakeSuite(CompatibilityVerdict verdict) : ICompatibilitySuite
{
    public int Runs { get; private set; }
    public string RuntimeId => "ollama";
    public string SuiteVersion => "test";
    public Task<CompatibilityReport> RunAsync(string model, string hardwareSummary, int requiredContext, IProgress<string>? progress, CancellationToken ct)
    {
        Runs++;
        var toolsOk = verdict == CompatibilityVerdict.AgenticCompatible;
        return Task.FromResult(new CompatibilityReport(model, "sha256:" + Hashing.Sha256Hex(model)[..12], "ollama", "0.40.6", SuiteVersion, hardwareSummary, DateTimeOffset.UtcNow,
            [new("Basic generation", verdict != CompatibilityVerdict.Failed, "", TimeSpan.Zero), new("Tool call", toolsOk, "", TimeSpan.Zero), new("JSON output", toolsOk, "", TimeSpan.Zero)],
            verdict, 30, 200));
    }
}

/// <summary>A process executor that answers RunAsync from a script (records every spec).</summary>
public sealed class ScriptedExecutor(Func<ProcessSpec, ProcessResult> handler) : IProcessExecutor
{
    public List<ProcessSpec> Runs { get; } = [];
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct) { lock (Runs) Runs.Add(spec); return Task.FromResult(handler(spec)); }
    public OperationResultWithProcess Start(ProcessSpec spec) => new(false, "not supported", null);
    public IReadOnlyList<TrackedProcess> Tracked => [];
    public bool Stop(string trackedId) => false;
    public event EventHandler<TrackedProcess>? ProcessExited { add { } remove { } }
    public string Preview(ProcessSpec spec) => ProcessPreview.Render(spec.Executable, spec.Arguments);

    public static ProcessResult Ok(string stdout = "") => new(ProcessOutcome.Completed, 0, stdout, "", TimeSpan.FromMilliseconds(5), "ok");
    public static ProcessResult Exit(int code, string stdout = "") => new(ProcessOutcome.Completed, code, stdout, "", TimeSpan.FromMilliseconds(5), "exit " + code);
}

/// <summary>A tool provider with scripted state (detect, available version, outcome).</summary>
public sealed class FakeToolProvider : IAgentToolProvider
{
    public AgentToolDescriptor Descriptor { get; set; } = new()
    {
        Id = "fake-tool", DisplayName = "Fake Tool", Publisher = "Tests", Description = "A tool for tests.", InstallMethod = "test",
        Source = new PackageSource("winget", "Tests.FakeTool", "winget", "Tests", "https://example.org", "MIT"),
        LatestVerifiedVersion = "1.2.0", MinimumSupportedVersion = "1.0.0", EstimatedDiskBytes = 1000,
    };
    public ToolStatus Status { get; set; } = new("fake-tool", ToolInstallState.NotInstalled, null, null, ToolAuthState.NotRequired, ComponentHealth.Unavailable, "Not installed");
    public string? Available { get; set; } = "1.2.0";
    public ToolStatus? AfterExecute { get; set; }
    public bool ExecuteSucceeds { get; set; } = true;
    public int Executions { get; private set; }
    public string PlanCommand { get; set; } = "winget install --id Tests.FakeTool";

    public Task<ToolStatus> DetectAsync(CancellationToken ct) => Task.FromResult(Status);
    public Task<IReadOnlyList<PrerequisiteCheck>> CheckPrerequisitesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PrerequisiteCheck>>([new("ok", true, "")]);
    public Task<string?> GetAvailableVersionAsync(CancellationToken ct) => Task.FromResult(Available);
    public Task<InstallPlan> PlanAsync(InstallOperation operation, CancellationToken ct) => Task.FromResult(new InstallPlan
    {
        ToolId = Descriptor.Id, Operation = operation, DisplayName = Descriptor.DisplayName, Publisher = Descriptor.Publisher, Source = Descriptor.Source!.Display,
        Version = Available ?? "latest", Method = "winget", Privileges = "user", Authentication = "none", Licence = "MIT", Rollback = "uninstall",
        CommandPreview = PlanCommand, Prerequisites = Status.Installed && operation == InstallOperation.Install ? [new("Not already installed", false, "installed")] : [],
    });
    public Task<InstallOutcome> ExecuteAsync(InstallPlan plan, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        Executions++;
        if (AfterExecute is not null) Status = AfterExecute;
        return Task.FromResult(new InstallOutcome(ExecuteSucceeds, ExecuteSucceeds ? "done" : "winget failed"));
    }
}

/// <summary>A download provider with scripted behaviour.</summary>
public sealed class FakeDownloadProvider(string id = "fake", bool resumable = false) : IDownloadProvider
{
    public string Id => id;
    public bool SupportsResume => resumable;
    public Func<DownloadRequest, IProgress<DownloadProgress>, CancellationToken, Task<OperationResult>> Behaviour { get; set; } =
        (_, p, _) => { p.Report(new DownloadProgress(DownloadPhase.Downloading, "downloading", 50, 100)); return Task.FromResult(OperationResult.Ok("downloaded")); };
    public Task<OperationResult> ExecuteAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken ct) => Behaviour(request, progress, ct);
}

public sealed class FakeVerifier(ItemVerdict verdict, string provider = "fake") : IInstalledItemVerifier
{
    public int Calls { get; private set; }
    public bool Handles(DownloadRequest request) => request.ProviderId == provider;
    public Task<ItemVerification> VerifyAsync(DownloadRequest request, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new ItemVerification(verdict, "verified: " + verdict, ["check"], @"C:\somewhere"));
    }
}

public static class Wait
{
    /// <summary>Polls a condition (the download queue runs on the thread pool).</summary>
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }
}
