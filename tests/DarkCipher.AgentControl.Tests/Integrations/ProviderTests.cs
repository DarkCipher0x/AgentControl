using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Integrations.Agents;
using DarkCipher.AgentControl.Integrations.HuggingFace;
using DarkCipher.AgentControl.Integrations.Ollama;

namespace DarkCipher.AgentControl.Tests.Integrations;

public sealed class NoProcessesFake : IProcessExecutor
{
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct) => Task.FromResult(new ProcessResult(ProcessOutcome.Completed, 0, "1.0.0", "", TimeSpan.Zero, "ok"));
    public OperationResultWithProcess Start(ProcessSpec spec) => new(false, "not in tests", null);
    public IReadOnlyList<TrackedProcess> Tracked => [];
    public bool Stop(string trackedId) => false;
    public event EventHandler<TrackedProcess>? ProcessExited { add { } remove { } }
    public string Preview(ProcessSpec spec) => spec.Executable;
}

public class ClaudeCodeProviderTests
{
    private static readonly AgentInstallation Installed = new("claude-code", true, @"C:\fake\claude.exe", "2.1.296", ComponentHealth.Healthy, "ok", []);
    private readonly ClaudeCodeProvider _p = new(new NoProcessesFake());

    private AgentLaunchPlan Plan(AgentMode mode, string profile = BuiltInPermissionProfiles.AskBeforeChangesId, string? model = "gpt-oss-64k", Uri? endpoint = null) =>
        _p.BuildLaunchPlan(new AgentLaunchRequest
        {
            Installation = Installed, Mode = mode, ProjectDirectory = @"C:\work\proj", Permissions = BuiltInPermissionProfiles.Get(profile),
            Model = model, Endpoint = endpoint, ContextLength = 65536,
        });

    [Fact]
    public void Gateway_mode_uses_documented_env_and_clears_the_api_key()
    {
        var plan = Plan(AgentMode.ExperimentalLocalGateway);
        Assert.Equal("http://127.0.0.1:11434", plan.Process.Environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal("ollama", plan.Process.Environment["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Contains("ANTHROPIC_API_KEY", plan.Process.ClearEnvironment);
        Assert.Equal("1", plan.Process.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.Equal("65536", plan.Process.Environment["CLAUDE_CODE_MAX_CONTEXT_TOKENS"]);
        Assert.Contains("--model", plan.Process.Arguments);
        Assert.Contains("NOT Claude", plan.BackendDescription, StringComparison.Ordinal);
        Assert.Equal(FeatureMaturity.Experimental, plan.Maturity);
        Assert.Contains(plan.Warnings, w => w.Contains("does not support", StringComparison.Ordinal));
    }

    [Fact]
    public void Native_mode_changes_no_environment()
    {
        var plan = Plan(AgentMode.Native, model: null);
        Assert.Empty(plan.Process.Environment);
        Assert.Empty(plan.Process.ClearEnvironment);
        Assert.Equal(FeatureMaturity.Supported, plan.Maturity);
    }

    [Fact]
    public void Remote_gateway_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => Plan(AgentMode.ExperimentalLocalGateway, endpoint: new Uri("http://192.168.1.5:11434")));

    [Fact]
    public void Invalid_model_names_are_refused() =>
        Assert.Throws<ArgumentException>(() => Plan(AgentMode.ExperimentalLocalGateway, model: "x; rm -rf /"));

    [Theory]
    [InlineData(BuiltInPermissionProfiles.ReadOnlyId, "plan")]
    [InlineData(BuiltInPermissionProfiles.AskBeforeChangesId, "default")]
    [InlineData(BuiltInPermissionProfiles.StandardId, "acceptEdits")]
    [InlineData(BuiltInPermissionProfiles.AdvancedId, "acceptEdits")]
    public void Permission_profiles_map_to_documented_modes_and_never_bypass(string profile, string mode)
    {
        var args = Plan(AgentMode.Native, profile, model: null).Process.Arguments;
        Assert.Equal(mode, args[args.ToList().IndexOf("--permission-mode") + 1]);
        Assert.DoesNotContain("bypassPermissions", args);
        Assert.DoesNotContain("--dangerously-skip-permissions", args);
    }

    [Fact]
    public void Read_only_disallows_shell_web_and_edit_tools()
    {
        var args = Plan(AgentMode.Native, BuiltInPermissionProfiles.ReadOnlyId, model: null).Process.Arguments.ToList();
        var tools = args[args.IndexOf("--disallowedTools") + 1];
        foreach (var t in new[] { "Bash", "WebFetch", "Edit", "Write" }) Assert.Contains(t, tools, StringComparison.Ordinal);
    }

    [Fact]
    public void Sessions_are_interactive_and_evaluated_as_agent() =>
        Assert.Equal(PathActor.Agent, Plan(AgentMode.Native, model: null).Process.Actor);
}

public class CodexProviderTests
{
    private static readonly AgentInstallation Installed = new("codex", true, @"C:\fake\codex.exe", "0.140.0", ComponentHealth.Healthy, "ok", []);
    private readonly CodexProvider _p = new(new NoProcessesFake());

    private AgentLaunchPlan Plan(string profile, AgentMode mode = AgentMode.LocalProvider) => _p.BuildLaunchPlan(new AgentLaunchRequest
    {
        Installation = Installed, Mode = mode, ProjectDirectory = @"C:\work\proj", Permissions = BuiltInPermissionProfiles.Get(profile),
        Model = "gpt-oss:20b", ContextLength = 32768, ProfileDirectory = @"C:\data\agent-profiles\stack1",
    });

    [Fact]
    public void Local_mode_uses_an_isolated_codex_home_with_documented_keys()
    {
        var plan = Plan(BuiltInPermissionProfiles.StandardId);
        Assert.Equal(@"C:\data\agent-profiles\stack1", plan.Process.Environment["CODEX_HOME"]);
        Assert.Equal(["--oss", "-m", "gpt-oss:20b"], plan.Process.Arguments);
        var cfg = Assert.Single(plan.FileChanges);
        Assert.StartsWith(@"C:\data\agent-profiles\stack1", cfg.Path, StringComparison.Ordinal);
        Assert.Contains("oss_provider = \"ollama\"", cfg.NewContent, StringComparison.Ordinal);
        Assert.Contains("model = \"gpt-oss:20b\"", cfg.NewContent, StringComparison.Ordinal);
        Assert.Contains("model_context_window = 32768", cfg.NewContent, StringComparison.Ordinal);
        Assert.Contains("sandbox_mode = \"workspace-write\"", cfg.NewContent, StringComparison.Ordinal);
        Assert.Contains("approval_policy = \"on-request\"", cfg.NewContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BuiltInPermissionProfiles.ReadOnlyId)]
    [InlineData(BuiltInPermissionProfiles.AskBeforeChangesId)]
    [InlineData(BuiltInPermissionProfiles.WorkspaceOnlyId)]
    [InlineData(BuiltInPermissionProfiles.StandardId)]
    [InlineData(BuiltInPermissionProfiles.AdvancedId)]
    public void Never_generates_full_access_or_no_approval(string profile)
    {
        var cfg = Plan(profile).FileChanges.Single().NewContent;
        Assert.DoesNotContain("danger-full-access", cfg, StringComparison.Ordinal);
        Assert.DoesNotContain("\"never\"", cfg, StringComparison.Ordinal);
        var native = Plan(profile, AgentMode.Native).Process.Arguments;
        Assert.DoesNotContain("danger-full-access", native);
        Assert.DoesNotContain("never", native);
    }

    [Fact]
    public void Read_only_profiles_use_the_read_only_sandbox() =>
        Assert.Contains("sandbox_mode = \"read-only\"", Plan(BuiltInPermissionProfiles.ReadOnlyId).FileChanges.Single().NewContent, StringComparison.Ordinal);

    [Fact]
    public void Toml_strings_are_escaped()
    {
        var toml = CodexProvider.ConfigToml("a\"b\\c", 4096, "read-only", "on-request");
        Assert.Contains("model = \"a\\\"b\\\\c\"", toml, StringComparison.Ordinal);
    }
}

public class OllamaRuntimeTests
{
    private const string Tags = """
    {"models":[{"name":"gpt-oss-64k:latest","model":"gpt-oss-64k:latest","modified_at":"2026-10-18T17:11:20.0984045+01:00","size":13793441582,"digest":"61c4","details":{"parent_model":"gpt-oss:20b","format":"gguf","family":"gpt-oss","parameter_size":"20.9B","quantization_level":"MXFP4","context_length":131072},"capabilities":["completion","tools","thinking"]},
    {"name":"gemma3:4b","model":"gemma3:4b","size":3338801804,"digest":"a2af","details":{"family":"gemma3","parameter_size":"4.3B","quantization_level":"Q4_K_M"},"capabilities":["completion","vision"]}]}
    """;

    private static (OllamaRuntime Runtime, FakeNetwork Net) Make()
    {
        var net = new FakeNetwork();
        return (new OllamaRuntime(net, new OllamaDetector(() => [])), net);
    }

    [Fact]
    public async Task Parses_tags_including_capabilities()
    {
        var (rt, net) = Make();
        net.Handler = r => FakeNetwork.Json(Tags);
        var models = await rt.ListModelsAsync(CancellationToken.None);
        var g = models.Single(m => m.Name == "gpt-oss-64k:latest");
        Assert.True(g.Supports(ModelCapabilities.Tools));
        Assert.Equal(131072, g.ContextLength);
        Assert.Equal("MXFP4", g.Quantization);
        Assert.False(models.Single(m => m.Name == "gemma3:4b").Supports(ModelCapabilities.Tools));
        Assert.All(net.Requests, r => Assert.Equal(NetworkPurpose.RuntimeManagement, r.Purpose));
    }

    [Fact]
    public async Task Pull_reports_progress_and_requires_success()
    {
        var (rt, net) = Make();
        net.StreamHandler = _ => ["{\"status\":\"pulling manifest\"}", "{\"status\":\"pulling abc\",\"digest\":\"abc\",\"total\":100,\"completed\":50}", "{\"status\":\"verifying sha256 digest\"}", "{\"status\":\"success\"}"];
        var seen = new List<PullProgress>();
        var r = await rt.PullAsync("qwen3:8b", new SyncProgress<PullProgress>(seen.Add), CancellationToken.None);
        Assert.True(r.Success, r.Message);
        Assert.Contains(seen, p => p.Fraction == 0.5);
    }

    [Fact]
    public async Task Pull_surfaces_errors_and_incomplete_streams()
    {
        var (rt, net) = Make();
        net.StreamHandler = _ => ["{\"error\":\"pull model manifest: file does not exist\"}"];
        Assert.False((await rt.PullAsync("nope:1b", new SyncProgress<PullProgress>(_ => { }), CancellationToken.None)).Success);
        net.StreamHandler = _ => ["{\"status\":\"pulling abc\",\"total\":10,\"completed\":1}"];
        Assert.False((await rt.PullAsync("nope:1b", new SyncProgress<PullProgress>(_ => { }), CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Delete_refuses_a_loaded_model()
    {
        var (rt, net) = Make();
        net.Handler = r => r.Uri.AbsolutePath == "/api/ps"
            ? FakeNetwork.Json("""{"models":[{"name":"gpt-oss-64k:latest","size":12802536242,"size_vram":12802536242,"context_length":65536}]}""")
            : FakeNetwork.Json("{}");
        var result = await rt.DeleteAsync("gpt-oss-64k:latest", CancellationToken.None);
        Assert.False(result.Success);
        Assert.DoesNotContain(net.Requests, r => r.Uri.AbsolutePath == "/api/delete");
    }

    [Fact]
    public async Task Status_reports_unavailable_when_not_running()
    {
        var (rt, net) = Make();
        net.Handler = _ => throw new HttpRequestException("connection refused");
        var s = await rt.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ComponentHealth.Unavailable, s.Health);
    }

    [Fact]
    public void Exposure_check_warns_when_listening_on_all_interfaces()
    {
        var d = new OllamaDetector(() => [new System.Net.IPEndPoint(System.Net.IPAddress.Any, 11434)]);
        Assert.NotNull(d.CheckExposure(11434));
        var safe = new OllamaDetector(() => [new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 11434)]);
        Assert.Null(safe.CheckExposure(11434));
    }

    [Theory]
    [InlineData("gpt-oss:20b", true)]
    [InlineData("hf.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF:Q4_K_M", true)]
    [InlineData("library/qwen3", true)]
    [InlineData("a b", false)]
    [InlineData("x;calc", false)]
    [InlineData("../../etc", false)]
    [InlineData("--help", false)]
    [InlineData("", false)]
    public void Model_name_rules(string name, bool ok) => Assert.Equal(ok, ModelNameRules.IsValid(name));
}

public class HuggingFaceTests
{
    [Theory]
    [InlineData("bartowski/Qwen2.5-Coder-7B-Instruct-GGUF", true)]
    [InlineData("owner/name", true)]
    [InlineData("../etc", false)]
    [InlineData("owner/../x", false)]
    [InlineData("a/b/c", false)]
    [InlineData("owner", false)]
    public void Repo_id_validation(string id, bool ok) => Assert.Equal(ok, HuggingFaceSource.IsValidRepoId(id));

    [Theory]
    [InlineData("model-Q4_K_M.gguf", "Q4_K_M")]
    [InlineData("Qwen2.5-Coder-7B-Instruct-IQ4_XS.gguf", "IQ4_XS")]
    [InlineData("model.Q8_0.gguf", "Q8_0")]
    [InlineData("gpt-oss-20b-MXFP4.gguf", "MXFP4")]
    [InlineData("model-f16.gguf", "F16")]
    [InlineData("README.md", null)]
    public void Quantisation_from_file_name(string file, string? quant) => Assert.Equal(quant, GgufQuant.FromFileName(file));

    [Fact]
    public async Task Search_uses_official_api_and_filters_invalid_ids()
    {
        var net = new FakeNetwork { Handler = _ => FakeNetwork.Json("""[{"id":"good/repo-GGUF","downloads":5,"tags":["gguf","license:apache-2.0"]},{"id":"bad id"}]""") };
        var hf = new HuggingFaceSource(net, () => null);
        var r = await hf.SearchAsync(new ModelSearchQuery("coder", GgufOnly: true), CancellationToken.None);
        var e = Assert.Single(r);
        Assert.Equal(LicenseClass.OpenSource, e.License);
        var req = Assert.Single(net.Requests);
        Assert.Equal("huggingface.co", req.Uri.Host);
        Assert.Equal(NetworkPurpose.ModelCatalog, req.Purpose);
        Assert.Contains("filter=gguf", req.Uri.Query, StringComparison.Ordinal);
    }
}

/// <summary>Synchronous IProgress for deterministic tests.</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
