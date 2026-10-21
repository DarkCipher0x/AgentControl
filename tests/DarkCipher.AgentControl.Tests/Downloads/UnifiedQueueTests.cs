using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Models;
using DarkCipher.AgentControl.Application.Recommendations;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.Tests.Downloads;

public class UnifiedQueueTests
{
    private static DownloadManager Queue(IDownloadProvider p, IInstalledItemVerifier? v = null, IDocumentStore? store = null, Func<long?>? free = null) =>
        new([p], new MemoryLog(), new MemoryAudit(), free ?? (() => null), v is null ? null : [v], store);

    [Fact]
    public async Task Downloaded_bytes_alone_never_make_an_item_ready()
    {
        var q = Queue(new FakeDownloadProvider());
        var job = q.Enqueue(new DownloadRequest("fake", "m", "Model")).Value!;
        await Wait.UntilAsync(() => job.IsFinished);
        Assert.Equal(DownloadPhase.ReadyWithLimitations, job.Phase);   // no post-install test exists for this provider
        Assert.Contains("no post-install test", job.VerificationResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verifier_decides_ready_and_failed()
    {
        var ok = Queue(new FakeDownloadProvider(), new FakeVerifier(ItemVerdict.Ready));
        var j1 = ok.Enqueue(new DownloadRequest("fake", "m", "Model")).Value!;
        await Wait.UntilAsync(() => j1.IsFinished);
        Assert.Equal(DownloadPhase.Ready, j1.Phase);
        Assert.Equal(@"C:\somewhere", j1.FinalLocation);

        var bad = Queue(new FakeDownloadProvider(), new FakeVerifier(ItemVerdict.Failed));
        var j2 = bad.Enqueue(new DownloadRequest("fake", "m", "Model")).Value!;
        await Wait.UntilAsync(() => j2.IsFinished);
        Assert.Equal(DownloadPhase.Failed, j2.Phase);
        Assert.NotNull(j2.Error);
    }

    [Fact]
    public async Task Duplicates_are_merged_and_disk_space_is_checked_first()
    {
        var gate = new TaskCompletionSource();
        var q = Queue(new FakeDownloadProvider { Behaviour = async (_, _, ct) => { await gate.Task.WaitAsync(ct); return OperationResult.Ok(); } }, free: () => 1_000);
        var a = q.Enqueue(new DownloadRequest("fake", "same", "A")).Value!;
        var b = q.Enqueue(new DownloadRequest("fake", "same", "A"));
        Assert.Same(a, b.Value);
        Assert.False(q.Enqueue(new DownloadRequest("fake", "big", "Big", ExpectedBytes: 10_000)).Success);
        gate.SetResult();
        await Wait.UntilAsync(() => a.IsFinished);
    }

    [Fact]
    public async Task Pause_and_resume_only_for_resumable_providers()
    {
        var calls = 0;
        var release = new TaskCompletionSource();
        var p = new FakeDownloadProvider("res", resumable: true)
        {
            Behaviour = async (_, prog, ct) =>
            {
                calls++;
                prog.Report(new DownloadProgress(DownloadPhase.Downloading, "layer", 10, 100));
                if (calls == 1) await Task.Delay(Timeout.Infinite, ct);
                await release.Task;
                return OperationResult.Ok();
            },
        };
        var q = Queue(p);
        var job = q.Enqueue(new DownloadRequest("res", "m", "M")).Value!;
        await Wait.UntilAsync(() => job.Phase == DownloadPhase.Downloading);
        Assert.True(q.Pause(job.Id).Success);
        await Wait.UntilAsync(() => job.IsPaused);
        release.SetResult();
        Assert.True(q.Resume(job.Id).Success);
        var resumed = q.Jobs.Single();
        await Wait.UntilAsync(() => resumed.IsFinished);
        Assert.Equal(2, calls);

        var q2 = Queue(new FakeDownloadProvider("nores") { Behaviour = async (_, prog, ct) => { prog.Report(new DownloadProgress(DownloadPhase.Downloading, "x", 1, 2)); await Task.Delay(200, ct); return OperationResult.Ok(); } });
        var j2 = q2.Enqueue(new DownloadRequest("nores", "m", "M")).Value!;
        await Wait.UntilAsync(() => j2.Phase == DownloadPhase.Downloading);
        Assert.False(q2.Pause(j2.Id).Success);
        await Wait.UntilAsync(() => j2.IsFinished);
    }

    [Fact]
    public async Task Cancel_retry_and_remove()
    {
        var q = Queue(new FakeDownloadProvider { Behaviour = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return OperationResult.Ok(); } });
        var job = q.Enqueue(new DownloadRequest("fake", "m", "M")).Value!;
        await Wait.UntilAsync(() => job.IsActive);
        q.Cancel(job.Id);
        await Wait.UntilAsync(() => job.IsFinished);
        Assert.Equal(DownloadPhase.Cancelled, job.Phase);
        Assert.True(q.Remove(job.Id).Success);
        Assert.Empty(q.Jobs);
    }

    [Fact]
    public async Task Queue_history_survives_restart_and_running_items_become_interrupted()
    {
        var store = new MemoryStore();
        var q = Queue(new FakeDownloadProvider { Behaviour = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return OperationResult.Ok(); } }, store: store);
        var job = q.Enqueue(new DownloadRequest("fake", "m", "M") { Kind = QueueItemKind.ModelPull, Source = "Ollama library" }).Value!;
        await Wait.UntilAsync(() => job.IsActive);
        var restarted = Queue(new FakeDownloadProvider(), store: store);
        var restored = restarted.Jobs.Single();
        Assert.Equal(DownloadPhase.Interrupted, restored.Phase);
        Assert.Equal(job.Id, restored.Id);
        Assert.Equal("Ollama library", restored.Request.Source);
        Assert.True(restored.IsFailed);
        q.Cancel(job.Id);
    }

    [Fact]
    public void Stage_labels_use_the_specification_names()
    {
        var job = new DownloadJob(new DownloadRequest("x", "y", "z"));
        Assert.Equal("Queued", job.StageLabel);
        Assert.Equal("Not installed", job.InstallState);
        Assert.Equal("Model (Ollama)", job.TypeLabel);
    }
}

public class ModelPipelineTests
{
    [Fact]
    public async Task Model_not_listed_after_download_fails()
    {
        var v = new ModelVerificationService(new FakeRuntime(), new CompatibilityStore(new MemoryStore()), new FakeSuite(CompatibilityVerdict.AgenticCompatible));
        var r = await v.VerifyModelAsync("ghost:1b", true, null, CancellationToken.None);
        Assert.Equal(ItemVerdict.Failed, r.Verdict);
    }

    [Fact]
    public async Task Untested_model_is_ready_with_limitations_tested_model_is_ready()
    {
        var rt = new FakeRuntime();
        rt.Models.Add(FakeRuntime.Model("gpt-oss:20b"));
        var store = new CompatibilityStore(new MemoryStore());
        var suite = new FakeSuite(CompatibilityVerdict.AgenticCompatible);
        var v = new ModelVerificationService(rt, store, suite);
        Assert.Equal(ItemVerdict.ReadyWithLimitations, (await v.VerifyModelAsync("gpt-oss:20b", false, null, CancellationToken.None)).Verdict);
        Assert.Equal(0, suite.Runs);
        var tested = await v.VerifyModelAsync("gpt-oss:20b", true, null, CancellationToken.None);
        Assert.Equal(ItemVerdict.Ready, tested.Verdict);
        Assert.NotNull(store.Latest("gpt-oss:20b"));
    }

    [Fact]
    public async Task Text_only_models_are_labelled_exactly()
    {
        var rt = new FakeRuntime();
        rt.Models.Add(FakeRuntime.Model("small:1b", ModelCapabilities.Completion));
        var r = await new ModelVerificationService(rt, new CompatibilityStore(new MemoryStore()), new FakeSuite(CompatibilityVerdict.TextGenerationOnly))
            .VerifyModelAsync("small:1b", true, null, CancellationToken.None);
        Assert.Equal(ItemVerdict.ReadyWithLimitations, r.Verdict);
        Assert.Equal("Text Generation Available / Agentic Compatibility Failed", r.Label);
    }

    [Fact]
    public async Task Pull_through_the_queue_is_ready_only_after_the_suite_passes()
    {
        var rt = new FakeRuntime();
        rt.Models.Add(FakeRuntime.Model("qwen3:14b"));
        var verifier = new ModelVerificationService(rt, new CompatibilityStore(new MemoryStore()), new FakeSuite(CompatibilityVerdict.AgenticCompatible));
        var q = new DownloadManager([new FakeDownloadProvider("ollama")], new MemoryLog(), new MemoryAudit(), () => null, [verifier]);
        var job = q.Enqueue(new DownloadRequest("ollama", "qwen3:14b", "Qwen3") { Kind = QueueItemKind.ModelPull, VerifyForAgents = true }).Value!;
        await Wait.UntilAsync(() => job.IsFinished);
        Assert.Equal(DownloadPhase.Ready, job.Phase);
        Assert.Contains(job.Log, l => l.Contains("PASS", StringComparison.Ordinal));
    }

    [Fact]
    public void Planner_blocks_invalid_names_low_disk_and_template_mismatch()
    {
        var curated = ModelCatalog.Curated.First(m => m.OllamaName == "qwen2.5-coder:7b");
        Assert.False(ModelInstallPlanner.ForOllamaTag("bad name; rm", null, null, 1L << 40).CanProceed);
        Assert.False(ModelInstallPlanner.ForOllamaTag(curated.OllamaName, curated, null, 1_000).CanProceed);
        Assert.False(ModelInstallPlanner.ForOllamaTag(curated.OllamaName, curated, null, 1L << 40, runtimeHealthy: false).CanProceed);
        var bigContext = new TemplateDefinition { Id = "big", Version = "1.0.0", DisplayName = "Big", PermissionProfileId = "read-only", MinimumContext = 200_000 };
        var plan = ModelInstallPlanner.ForOllamaTag(curated.OllamaName, curated, null, 1L << 40, bigContext);
        Assert.False(plan.CanProceed);
        Assert.Contains(plan.Steps, s => s.Number == 6 && s.State == PipelineStepState.Blocked);
        var ok = ModelInstallPlanner.ForOllamaTag(curated.OllamaName, curated, null, 1L << 40);
        Assert.True(ok.CanProceed);
        Assert.Equal(10, ok.Steps.Count);
    }

    [Fact]
    public void Hugging_Face_gated_or_non_gguf_repositories_have_no_install_path()
    {
        var entry = new ModelSourceEntry("org/model", "Model", "org", LicenseClass.OpenSource, "apache-2.0", Gated: true, Private: false, 10, [], "https://huggingface.co/org/model", "text-generation");
        var gated = new ModelSourceDetails(entry, [new ModelFile("model-Q4_K_M.gguf", 4_000_000_000, "Q4_K_M", "abc")], null, "org/model");
        var hw = TestHardware.Profile();
        var hf = HfImportPlanner.Plan(gated, hw, 1L << 40);
        Assert.Equal(ImportFeasibility.Unsupported, hf.Feasibility);
        Assert.False(ModelInstallPlanner.ForHuggingFace(hf, gated, hw).CanProceed);

        var safetensors = new ModelSourceDetails(entry with { Gated = false }, [new ModelFile("model.safetensors", 4_000_000_000, null, "abc")], null, "org/model");
        var hf2 = HfImportPlanner.Plan(safetensors, hw, 1L << 40);
        Assert.Equal(ImportFeasibility.Unsupported, hf2.Feasibility);
        Assert.False(ModelInstallPlanner.ForHuggingFace(hf2, safetensors, hw).CanProceed);
    }
}

internal static class TestHardware
{
    public static Domain.Hardware.HardwareProfile Profile() => Application.Hw.Profile(16L * 1024 * 1024 * 1024, 32L * 1024 * 1024 * 1024);
}
