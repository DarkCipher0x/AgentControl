using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Recommendations;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Infrastructure.Hardware;
using DarkCipher.AgentControl.Security.Permissions;

namespace DarkCipher.AgentControl.Tests.Application;

public static class Hw
{
    public static HardwareProfile Profile(long vramBytes, long ramBytes, GpuVendor vendor = GpuVendor.Amd, bool parsec = true)
    {
        var gpus = new List<GpuInfo>();
        if (vramBytes > 0) gpus.Add(new GpuInfo("AMD Radeon RX 9070 XT", vendor, vramBytes, "driver registry (qwMemorySize)", "32.0", DateTimeOffset.UtcNow.AddMonths(-2), false, DetectionState.Detected, "ROCm / Vulkan"));
        if (parsec) gpus.Add(new GpuInfo("Parsec Virtual Display Adapter", GpuVendor.Virtual, 0, "WMI", "0.45", null, true, DetectionState.Unsupported, null));
        return new HardwareProfile(new OsInfo("Windows 11 Pro", "10.0.26300", "26300", "64-bit"), new CpuInfo("Ryzen 7 9800X3D", 8, 16, ["AVX2", "AVX-512F"]),
            new MemoryInfo(ramBytes, ramBytes / 2), gpus, [], [], DateTimeOffset.UtcNow, []);
    }
}

public class HardwareTests
{
    [Fact]
    public void Sixteen_gig_gpu_rates_excellent_and_ignores_parsec()
    {
        var a = HardwareAssessor.Assess(Hw.Profile(17_095_983_104, 34_000_000_000));
        Assert.Equal(HardwareRating.ExcellentForLocalAi, a.Rating);
        Assert.Contains(a.Reasons, r => r.Contains("Parsec", StringComparison.Ordinal));
    }

    [Fact]
    public void No_gpu_low_ram_is_memory_limited() => Assert.Equal(HardwareRating.MemoryLimited, HardwareAssessor.Assess(Hw.Profile(0, 8L * Bytes.GiB)).Rating);

    [Fact]
    public void No_gpu_enough_ram_is_cpu_only() => Assert.Equal(HardwareRating.CpuOnly, HardwareAssessor.Assess(Hw.Profile(0, 32L * Bytes.GiB)).Rating);

    [Theory]
    [InlineData("Parsec Virtual Display Adapter", @"ROOT\DISPLAY\0000", true)]
    [InlineData("Microsoft Basic Display Adapter", @"PCI\VEN_1414", true)]
    [InlineData("AMD Radeon RX 9070 XT", @"PCI\VEN_1002&DEV_7550", false)]
    public void Virtual_adapters_are_detected(string name, string pnp, bool isVirtual) => Assert.Equal(isVirtual, GpuClassifier.IsVirtual(name, pnp));

    [Fact]
    public void Registry_memory_parsing_handles_qword_dword_and_binary()
    {
        Assert.Equal(17_095_983_104L, GpuClassifier.ParseRegistryMemory(17_095_983_104L));
        Assert.Equal(4_293_918_720L, GpuClassifier.ParseRegistryMemory(unchecked((int)4_293_918_720u)));
        Assert.Equal(17_095_983_104L, GpuClassifier.ParseRegistryMemory(BitConverter.GetBytes(17_095_983_104L)));
        Assert.Null(GpuClassifier.ParseRegistryMemory(null));
    }

    [Fact]
    public void Vendor_from_pci_ids() => Assert.Equal(GpuVendor.Amd, GpuClassifier.VendorFrom("Something", @"PCI\VEN_1002&DEV_7550"));

    [Theory]
    [InlineData("USB Input Device", "HIDClass", false)]
    [InlineData("Voicemeeter Input (VB-Audio Voicemeeter VAIO)", "MEDIA", false)]
    [InlineData("Corsair composite virtual input device", null, false)]
    [InlineData("Intel(R) AI Boost", "ComputeAccelerator", true)]
    [InlineData("Qualcomm(R) Hexagon(TM) NPU", null, true)]
    [InlineData("AMD IPU Device", null, true)]
    [InlineData("Neural Processing Unit", null, true)]
    public void Npu_detection_does_not_match_input_devices(string name, string? cls, bool expected) =>
        Assert.Equal(expected, NpuClassifier.IsNpu(name, cls));
}

public class RecommendationTests
{
    private readonly RecommendationEngine _e = new();

    [Fact]
    public void Agentic_coding_on_16gb_recommends_tool_models_that_fit_on_gpu()
    {
        var recs = _e.Recommend(Hw.Profile(17_095_983_104, 34_000_000_000), RecommendationProfile.AgenticCoding, []);
        Assert.NotEmpty(recs);
        Assert.All(recs, r =>
        {
            Assert.True(r.Model.Capabilities.HasFlag(ModelCapabilities.Tools));
            Assert.Equal(MemoryFit.FullyOnGpu, r.Fit);
            Assert.True(r.Model.MaxContext >= 32768);
        });
        Assert.DoesNotContain(recs, r => r.Model.OllamaName == "qwen2.5-coder:32b"); // 20 GB will not fit
    }

    [Fact]
    public void Cpu_only_machines_get_small_models_only()
    {
        var recs = _e.Recommend(Hw.Profile(0, 16L * Bytes.GiB, parsec: false), RecommendationProfile.CpuFriendly, []);
        Assert.NotEmpty(recs);
        Assert.All(recs, r => Assert.True(r.Model.ParametersBillions <= 4.5));
        Assert.All(recs, r => Assert.Equal(MemoryFit.CpuOnly, r.Fit));
    }

    [Fact]
    public void Models_without_tools_are_never_recommended_for_agents()
    {
        var recs = _e.Recommend(Hw.Profile(17_095_983_104, 34_000_000_000), RecommendationProfile.AgenticCoding, []);
        Assert.DoesNotContain(recs, r => r.Model.OllamaName.StartsWith("gemma3", StringComparison.Ordinal));
    }

    [Fact]
    public void Cloud_assisted_returns_no_local_model() =>
        Assert.Empty(_e.Recommend(Hw.Profile(17_095_983_104, 34_000_000_000), RecommendationProfile.CloudAssisted, []));

    [Fact]
    public void Estimates_include_kv_cache_growth_with_context()
    {
        var m = ModelCatalog.Curated.First(c => c.OllamaName == "qwen3:14b");
        Assert.True(RecommendationEngine.Estimate(m, 32768).KvCacheBytes > RecommendationEngine.Estimate(m, 8192).KvCacheBytes * 3);
    }

    [Fact]
    public void Measured_runtime_size_calibrates_the_estimate()
    {
        var m = ModelCatalog.Curated.First(c => c.OllamaName == "gpt-oss:20b");
        var observed = new RunningModel("gpt-oss:20b", 12_802_536_242, 12_802_536_242, 65536, null);
        var e = RecommendationEngine.Estimate(m, 65536, observed);
        Assert.True(e.Measured);
        Assert.InRange(e.TotalBytes, 12_000_000_000, 13_500_000_000);
    }

    [Fact]
    public void Reasons_never_contain_speed_claims()
    {
        foreach (var r in _e.Recommend(Hw.Profile(17_095_983_104, 34_000_000_000), RecommendationProfile.Balanced, []))
            Assert.DoesNotContain("tok/s", r.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Licence_classification_is_conservative()
    {
        Assert.Equal(LicenseClass.OpenSource, LicenseClassifier.Classify("apache-2.0"));
        Assert.Equal(LicenseClass.ResearchOnly, LicenseClassifier.Classify("cc-by-nc-4.0"));
        Assert.Equal(LicenseClass.Unknown, LicenseClassifier.Classify("other"));
        Assert.Equal(LicenseClass.Unknown, LicenseClassifier.Classify(null));
    }
}

public class HfImportPlannerTests
{
    private static ModelSourceDetails Details(bool gated = false, params (string Path, long Size)[] files) =>
        new(new ModelSourceEntry("owner/Model-GGUF", "Model-GGUF", "owner", LicenseClass.OpenSource, "apache-2.0", gated, false, 10, [], "https://huggingface.co/owner/Model-GGUF", null),
            files.Select(f => new ModelFile(f.Path, f.Size, DarkCipher.AgentControl.Integrations.HuggingFace.GgufQuant.FromFileName(f.Path), null)).ToList(), null, "owner/Model-GGUF by owner");

    [Fact]
    public void Chooses_the_preferred_quant_that_fits()
    {
        var plan = HfImportPlanner.Plan(Details(false, ("m-Q8_0.gguf", 8_000_000_000), ("m-Q4_K_M.gguf", 4_500_000_000), ("m-Q2_K.gguf", 2_000_000_000)),
            Hw.Profile(17_095_983_104, 34_000_000_000), 500_000_000_000);
        Assert.Equal(ImportFeasibility.Ready, plan.Feasibility);
        Assert.Equal("Q4_K_M", plan.Quantization);
        Assert.Equal("hf.co/owner/Model-GGUF:Q4_K_M", plan.OllamaReference);
        Assert.Contains(plan.Warnings, w => w.Contains("unverified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gated_split_and_non_gguf_repos_are_unsupported()
    {
        var hw = Hw.Profile(17_095_983_104, 34_000_000_000);
        Assert.Equal(ImportFeasibility.Unsupported, HfImportPlanner.Plan(Details(true, ("m-Q4_K_M.gguf", 4_000_000_000)), hw, long.MaxValue).Feasibility);
        Assert.Equal(ImportFeasibility.Unsupported, HfImportPlanner.Plan(Details(false, ("m-Q4_K_M-00001-of-00002.gguf", 4_000_000_000)), hw, long.MaxValue).Feasibility);
        Assert.Equal(ImportFeasibility.Unsupported, HfImportPlanner.Plan(Details(false, ("model.safetensors", 4_000_000_000)), hw, long.MaxValue).Feasibility);
    }

    [Fact]
    public void Insufficient_disk_is_refused() =>
        Assert.Equal(ImportFeasibility.Unsupported, HfImportPlanner.Plan(Details(false, ("m-Q4_K_M.gguf", 4_500_000_000)), Hw.Profile(17_095_983_104, 34_000_000_000), 1_000_000_000).Feasibility);
}

public class StackServiceTests
{
    private static StackService Make(MemoryAudit? audit = null) => new(new MemoryStore(), audit ?? new MemoryAudit(), new PermissionService(null, new MemoryAudit()));

    [Fact]
    public void Built_in_templates_are_valid_and_read_only()
    {
        var s = Make();
        foreach (var t in StackTemplates.BuiltIn) Assert.Empty(s.Validate(t));
        Assert.False(s.Save(StackTemplates.BuiltIn[0]).Success);
    }

    [Fact]
    public void Import_treats_profiles_as_untrusted()
    {
        var audit = new MemoryAudit();
        var s = Make(audit);
        var json = """{"Id":"evil","Name":"Totally safe","AgentProviderId":"codex","AgentMode":"LocalProvider","RuntimeId":"ollama","Endpoint":"http://127.0.0.1:11434","PrimaryModel":"gpt-oss:20b","PermissionProfileId":"advanced-development","Privacy":"CloudAllowed"}""";
        var r = s.Import(json);
        Assert.True(r.Success, r.Message);
        Assert.Equal(BuiltInPermissionProfiles.DefaultId, r.Value!.PermissionProfileId);
        Assert.Equal(PrivacyMode.FullyLocal, r.Value.Privacy);
        Assert.Contains(audit.Events, e => e.Action == "StackImported");
    }

    [Theory]
    [InlineData("""{"Id":"x","Name":"x","AgentProviderId":"codex","AgentMode":"LocalProvider","Endpoint":"http://attacker.example:11434","PrimaryModel":"m"}""")]
    [InlineData("""{"Id":"x","Name":"x","AgentProviderId":"codex","AgentMode":"LocalProvider","PrimaryModel":"m; calc"}""")]
    [InlineData("""{"SchemaVersion":99,"Id":"x","Name":"x","AgentProviderId":"codex"}""")]
    [InlineData("not json")]
    public void Import_rejects_malicious_or_invalid_profiles(string json) => Assert.False(Make().Import(json).Success);

    [Fact]
    public void Import_rejects_oversize_files() => Assert.False(Make().Import(new string(' ', 70_000)).Success);

    [Fact]
    public void Export_round_trips_without_secrets()
    {
        var s = Make();
        var json = s.Export("codex-ollama-balanced");
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(s.Import(json).Success);
    }
}
