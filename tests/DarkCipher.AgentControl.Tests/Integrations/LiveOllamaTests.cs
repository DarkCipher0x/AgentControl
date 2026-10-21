using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Integrations.Ollama;
using DarkCipher.AgentControl.Security.Network;
using Xunit.Abstractions;

namespace DarkCipher.AgentControl.Tests.Integrations;

/// <summary>
/// Opt-in end-to-end tests against the REAL local Ollama (loopback only, fixed test prompts, no downloads).
/// Run with: set DCAC_LIVE_OLLAMA=1 and DCAC_LIVE_MODEL=&lt;installed model&gt;, then dotnet test --filter Live.
/// Without the variables they return immediately.
/// </summary>
public class LiveOllamaTests(ITestOutputHelper output)
{
    private static string? Model => Environment.GetEnvironmentVariable("DCAC_LIVE_OLLAMA") == "1" ? Environment.GetEnvironmentVariable("DCAC_LIVE_MODEL") ?? "gpt-oss-64k" : null;

    private static OllamaRuntime Runtime() =>
        new(new NetworkSecurityService(new FixedPolicy(new SecurityPolicy()), new MemoryAudit(), null, null), new OllamaDetector());

    [Fact]
    public async Task Live_compatibility_suite()
    {
        if (Model is not { } model) return;
        var report = await new OllamaCompatibilitySuite(Runtime()).RunAsync(model, "live test", 32768, null, CancellationToken.None);
        foreach (var r in report.Results) output.WriteLine($"{(r.Passed ? "PASS" : "FAIL")} {r.Test} ({r.Duration.TotalSeconds:0.0}s): {r.Detail}");
        output.WriteLine($"Verdict {report.Verdict}; Ollama {report.RuntimeVersion}; tok/s {report.MeasuredTokensPerSecond:0.0}; TTFT {report.TimeToFirstTokenMs:0} ms");
        Assert.NotEqual(CompatibilityVerdict.NotRun, report.Verdict);
        Assert.True(report.Results.First(r => r.Test == "Connectivity").Passed);
    }

    /// <summary>The whole model pipeline through the unified queue on an ALREADY INSTALLED model: Ollama re-checks the
    /// manifest and every layer digest (nothing new is downloaded), then the Testing stage runs the real suite.</summary>
    [Fact]
    public async Task Live_model_pipeline_through_the_unified_queue()
    {
        if (Model is not { } model) return;
        var runtime = Runtime();
        var store = new global::DarkCipher.AgentControl.Application.Compatibility.CompatibilityStore(new MemoryStore());
        var verifier = new global::DarkCipher.AgentControl.Application.Models.ModelVerificationService(runtime, store, new OllamaCompatibilitySuite(runtime));
        var queue = new global::DarkCipher.AgentControl.Application.Downloads.DownloadManager([runtime], new MemoryLog(), new MemoryAudit(), () => null, [verifier]);
        var job = queue.Enqueue(new global::DarkCipher.AgentControl.Domain.Downloads.DownloadRequest("ollama", model, model) { Kind = global::DarkCipher.AgentControl.Domain.Downloads.QueueItemKind.ModelPull, VerifyForAgents = true }).Value!;
        await Wait.UntilAsync(() => job.IsFinished, (int)TimeSpan.FromMinutes(10).TotalMilliseconds);
        foreach (var l in job.Log) output.WriteLine(l);
        output.WriteLine($"Final: {job.StageLabel} — {job.Status} — {job.VerificationResult}");
        Assert.True(job.Phase is global::DarkCipher.AgentControl.Domain.Downloads.DownloadPhase.Ready or global::DarkCipher.AgentControl.Domain.Downloads.DownloadPhase.ReadyWithLimitations, job.Error ?? job.Status);
        Assert.NotNull(store.Latest(model));
    }

    [Fact]
    public async Task Live_anthropic_gateway_probe()
    {
        if (Model is not { } model) return;
        var gw = await new GatewayProbe(Runtime()).ProbeAnthropicAsync(model, CancellationToken.None);
        output.WriteLine($"{gw.Name} {gw.Version} at {gw.Address}: {gw.Compatibility} ({gw.Health})");
        foreach (var l in gw.Limitations) output.WriteLine("  " + l);
        Assert.NotEqual(CompatibilityVerdict.NotRun, gw.Compatibility);
    }
}
