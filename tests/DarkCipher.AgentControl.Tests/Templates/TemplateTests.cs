using System.Text.Json.Nodes;
using DarkCipher.AgentControl.Application.Templates;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Templates;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Agents;
using DarkCipher.AgentControl.Security.Permissions;
using DarkCipher.AgentControl.Tests.Integrations;

namespace DarkCipher.AgentControl.Tests.Templates;

public class TemplateValidationTests
{
    private static TemplateValidationService V() => new(new PermissionService(new MemoryStore(), new MemoryAudit()));

    [Fact]
    public void Every_built_in_template_is_valid_and_versioned()
    {
        var v = V();
        Assert.True(BuiltInTemplates.All.Count >= 12);
        foreach (var t in BuiltInTemplates.All)
        {
            var issues = v.Validate(t);
            Assert.True(issues.Count == 0, $"{t.Id}: {string.Join("; ", issues.Select(i => i.Field + " " + i.Problem))}");
            Assert.Equal(TemplateDefinition.CurrentSchemaVersion, t.SchemaVersion);
        }
        Assert.Equal(BuiltInTemplates.All.Count, BuiltInTemplates.All.Select(t => t.Id).Distinct().Count());
        foreach (var c in new[] { TemplateCategory.GeneralCoding, TemplateCategory.AgenticCoding, TemplateCategory.CodeReview, TemplateCategory.BugInvestigation,
                     TemplateCategory.LargeRepositoryAnalysis, TemplateCategory.Refactoring, TemplateCategory.Documentation, TemplateCategory.TestGeneration,
                     TemplateCategory.PlanningAndArchitecture, TemplateCategory.FastLocalAssistance, TemplateCategory.PrivateOfflineCoding,
                     TemplateCategory.HybridLocalAndCloud, TemplateCategory.Experimental })
            Assert.Contains(BuiltInTemplates.All, t => t.Category == c);
    }

    private static TemplateDefinition Good() => BuiltInTemplates.All.First(t => t.Id == "general-coding-local");

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("options")]
    [InlineData("permissionProfileId")]
    [InlineData("secrets")]
    [InlineData("validationTests")]
    [InlineData("context")]
    [InlineData("runtime")]
    public void Invalid_fields_are_rejected(string field)
    {
        var t = field switch
        {
            "id" => Good() with { Id = "Bad Id!" },
            "version" => Good() with { Version = "1.0" },
            "options" => Good() with { Options = new Dictionary<string, string> { ["executable"] = @"C:\evil.exe" } },
            "permissionProfileId" => Good() with { PermissionProfileId = "god-mode" },
            "secrets" => Good() with { SystemInstructions = "Use api_key = sk-abcdefghijklmnopqrstuvwxyz0123" },
            "validationTests" => Good() with { ValidationTests = ["rm-rf"] },
            "context" => Good() with { MinimumContext = 99_999, RecommendedContext = 4096 },
            _ => Good() with { Runtime = null },
        };
        Assert.Contains(V().Validate(t), i => i.Field == field);
    }

    [Fact]
    public void Unknown_json_fields_are_refused_so_templates_cannot_smuggle_executables()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(Good(), TemplateValidationService.Json)!.AsObject();
        json["executable"] = @"C:\Windows\System32\cmd.exe";
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => TemplateValidationService.Deserialize(json));
    }

    [Fact]
    public void Schema_1_stack_templates_migrate_with_safe_permissions()
    {
        var v1 = JsonNode.Parse("""
            { "id": "My Old Stack", "name": "Old stack", "agentProviderId": "codex", "agentMode": "LocalProvider", "runtimeId": "ollama",
              "endpoint": "http://10.0.0.5:11434", "primaryModel": "qwen3:14b", "contextLength": 65536, "permissionProfileId": "advanced-development" }
            """);
        var r = TemplateMigrationService.Migrate(v1);
        Assert.NotNull(r.Template);
        Assert.Equal("my-old-stack", r.Template!.Id);
        Assert.Equal(BuiltInPermissionProfiles.DefaultId, r.Template.PermissionProfileId);
        Assert.Equal(32768, r.Template.MinimumContext);
        Assert.Equal(65536, r.Template.RecommendedContext);
        Assert.Contains(r.Notes, n => n.Contains("remote endpoint", StringComparison.Ordinal));
        Assert.Empty(V().Validate(r.Template));
        Assert.NotNull(TemplateMigrationService.Migrate(JsonNode.Parse("""{ "schemaVersion": 9, "id": "x" }""")).Error);
    }
}

public class TemplateCatalogueTests
{
    private static TemplateCatalogueService Catalogue(MemoryStore? store = null) =>
        new(store ?? new MemoryStore(), new MemoryAudit(), new TemplateValidationService(new PermissionService(new MemoryStore(), new MemoryAudit())));

    [Fact]
    public void Built_ins_are_read_only_duplicates_are_editable_and_reset_restores_them()
    {
        var c = Catalogue();
        Assert.False(c.Save(BuiltInTemplates.All[0] with { DisplayName = "Hacked" }).Success);
        var dup = c.Duplicate("general-coding-local").Value!;
        Assert.Equal("general-coding-local", dup.DerivedFrom);
        Assert.True(c.Save(dup with { DefaultModel = "qwen3:14b" }).Success);
        Assert.True(c.ResetToDefault(dup.Id).Success);
        var reset = c.Find(dup.Id)!;
        Assert.Equal("gpt-oss:20b", reset.DefaultModel);
        Assert.Equal("1.0.1", reset.Version);
        Assert.True(c.Delete(dup.Id).Success);
        Assert.False(c.Delete("general-coding-local").Success);
    }

    [Fact]
    public void Export_and_import_round_trip_with_secret_exclusion_and_safe_permissions()
    {
        var a = Catalogue();
        var json = a.Export("code-review-local").Value!;
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        var b = Catalogue();
        var preview = b.PreviewImport(json);
        Assert.True(preview.Valid, preview.Message);
        Assert.NotEqual("code-review-local", preview.Template!.Id);                  // id collision → renamed
        Assert.Equal(BuiltInPermissionProfiles.DefaultId, preview.Template.PermissionProfileId);  // read-only reset to the safe default as for every import
        Assert.True(b.ApplyImport(preview).Success);
        Assert.Equal(TemplateOrigin.Imported, b.Find(preview.Template.Id)!.Origin);

        var node = JsonNode.Parse(json)!.AsObject();
        node["Description"] = "Use hf_abcdefghijklmnopqrstuvwxyz to download";
        var withSecret = node.ToJsonString();
        Assert.False(b.PreviewImport(withSecret).Valid);
        Assert.False(b.PreviewImport("{ broken").Valid);
    }

    [Fact]
    public void Extension_templates_never_choose_permissions()
    {
        var c = Catalogue();
        var t = BuiltInTemplates.All.First(x => x.Id == "agentic-coding-local") with { Id = "ext-template" };
        Assert.True(c.RegisterExtensionTemplate(t, "com.example").Success);
        Assert.Equal(BuiltInPermissionProfiles.DefaultId, c.Find("ext-template")!.PermissionProfileId);
        Assert.True(c.IsReadOnly("ext-template"));
        Assert.False(c.RegisterExtensionTemplate(BuiltInTemplates.All[0], "com.example").Success);   // cannot replace a built-in
    }
}

public class TemplateCompatibilityTests
{
    private static EnvironmentSnapshot Env(Action<Dictionary<string, EnvironmentSnapshot.AgentFact>>? agents = null, bool runtime = true, CompatibilityVerdict verdict = CompatibilityVerdict.AgenticCompatible,
        PrivacyMode privacy = PrivacyMode.FullyLocal, int modelCtx = 131072, PathDecisionKind? project = PathDecisionKind.Allow, bool withModel = true)
    {
        var a = new Dictionary<string, EnvironmentSnapshot.AgentFact>
        {
            ["codex"] = new("codex", true, "0.161.0", true, [AgentMode.Native, AgentMode.LocalProvider], ToolTrust.BuiltInVerified, "Codex 0.161.0"),
            ["claude-code"] = new("claude-code", true, "2.1.296", true, [AgentMode.Native, AgentMode.VerifiedLocalGateway, AgentMode.ExperimentalLocalGateway], ToolTrust.BuiltInVerified, "Claude Code"),
        };
        agents?.Invoke(a);
        return new EnvironmentSnapshot
        {
            Agents = a, RuntimeId = "ollama", RuntimeHealthy = runtime, RuntimeVersion = runtime ? "0.40.6" : null,
            Models = withModel ? [new("gpt-oss:20b", ModelCapabilities.Completion | ModelCapabilities.Tools, modelCtx, 13_000_000_000, "sha256:x")] : [],
            CompatibilityByModel = new Dictionary<string, CompatibilityVerdict>(StringComparer.OrdinalIgnoreCase) { ["gpt-oss:20b"] = verdict },
            Privacy = privacy, GpuBudgetBytes = 15_000_000_000, RamBudgetBytes = 48_000_000_000, FreeModelDiskBytes = 500_000_000_000,
            PermissionProfiles = BuiltInPermissionProfiles.All().Select(p => p.Id).ToHashSet(), ProjectDecision = project, ProjectReason = "project",
        };
    }

    private static TemplateDefinition T(string id) => BuiltInTemplates.All.First(t => t.Id == id);
    private static readonly TemplateSelection Codex = new() { AgentId = "codex", Model = "gpt-oss:20b", ProjectId = "p1" };

    [Fact]
    public void Complete_supported_stack_can_launch()
    {
        var r = TemplateCompatibilityService.Evaluate(T("general-coding-local"), Codex, Env());
        Assert.True(r.CanLaunch, string.Join("; ", r.Checks.Where(c => c.Outcome == CheckOutcome.Failed).Select(c => c.Name + ": " + c.Detail)));
        foreach (var name in new[] { "Agent Installed", "Agent Version", "API Protocol", "Runtime Installed", "Runtime Version", "Required Services", "Model Installed",
                     "Model Loaded or Loadable", "Hardware Capacity", "Model Capability", "Tool Calling", "Structured Output", "Context Support", "Privacy Mode",
                     "Permission Profile", "Working Directory", "Network Policy", "Authentication" })
            Assert.Contains(r.Checks, c => c.Name == name);
    }

    [Fact]
    public void Missing_agent_runtime_and_model_each_name_their_fix()
    {
        var noAgent = TemplateCompatibilityService.Evaluate(T("general-coding-local"), Codex, Env(a => a["codex"] = a["codex"] with { Installed = false }));
        Assert.False(noAgent.CanLaunch);
        Assert.Contains(noAgent.Checks, c => c.Name == "Agent Installed" && c.Fix == CorrectiveAction.InstallAgent);

        var noRuntime = TemplateCompatibilityService.Evaluate(T("general-coding-local"), Codex, Env(runtime: false));
        Assert.Contains(noRuntime.Checks, c => c.Name == "Runtime Installed" && c.Outcome == CheckOutcome.Failed);

        var noModel = TemplateCompatibilityService.Evaluate(T("general-coding-local"), Codex, Env(withModel: false));
        Assert.Contains(noModel.Checks, c => c.Name == "Model Installed" && c.Fix == CorrectiveAction.PullModel);
    }

    [Fact]
    public void Wrong_protocol_mode_is_rejected()
    {
        var r = TemplateCompatibilityService.Evaluate(T("general-coding-local"), Codex with { AgentMode = AgentMode.ExperimentalLocalGateway }, Env());
        Assert.Contains(r.Checks, c => c.Name == "API Protocol" && c.Outcome == CheckOutcome.Failed);
    }

    [Fact]
    public void Insufficient_context_and_failed_tool_use_block_launch()
    {
        var ctx = TemplateCompatibilityService.Evaluate(T("large-repo-analysis-local"), Codex, Env(modelCtx: 32768));
        Assert.Contains(ctx.Checks, c => c.Name == "Context Support" && c.Outcome == CheckOutcome.Failed && c.Fix == CorrectiveAction.ReduceContext);
        var tools = TemplateCompatibilityService.Evaluate(T("general-coding-local"), Codex, Env(verdict: CompatibilityVerdict.TextGenerationOnly));
        Assert.False(tools.CanLaunch);
        Assert.Contains(tools.Checks, c => c.Name == "Tool Calling" && c.Detail.Contains("Agentic Compatibility Failed", StringComparison.Ordinal));
        var untested = TemplateCompatibilityService.Evaluate(T("general-coding-local"), Codex, Env(verdict: CompatibilityVerdict.NotRun));
        Assert.True(untested.CanLaunch);
        Assert.True(untested.HasWarnings);
        Assert.Contains(untested.Checks, c => c.Fix == CorrectiveAction.RunCompatibilityTest);
    }

    [Fact]
    public void Privacy_project_and_custom_trust_rules()
    {
        var cloud = TemplateCompatibilityService.Evaluate(T("claude-native-hybrid"), new TemplateSelection { AgentId = "claude-code", ProjectId = "p" }, Env());
        Assert.Contains(cloud.Checks, c => c.Name == "Privacy Mode" && c.Outcome == CheckOutcome.Failed);
        var offline = TemplateCompatibilityService.Evaluate(T("private-offline-coding"), Codex, Env(privacy: PrivacyMode.CloudAllowed));
        Assert.Contains(offline.Checks, c => c.Name == "Privacy Mode" && c.Outcome == CheckOutcome.Failed);
        var denied = TemplateCompatibilityService.Evaluate(T("general-coding-local"), Codex, Env(project: PathDecisionKind.Deny));
        Assert.Contains(denied.Checks, c => c.Name == "Working Directory" && c.Outcome == CheckOutcome.Failed);
        var custom = Env(a => a["codex"] = a["codex"] with { Trust = ToolTrust.Unverified });
        Assert.Contains(TemplateCompatibilityService.Evaluate(T("general-coding-local"), Codex, custom).Checks, c => c.Fix == CorrectiveAction.ValidateCustomAgent);
    }

    [Fact]
    public void Filtering_offers_only_templates_for_the_selected_agent_and_model()
    {
        var all = BuiltInTemplates.All;
        var codex = TemplateCompatibilityService.Filter(all, new TemplateSelection { AgentId = "codex", Model = "gpt-oss:20b" }, Env());
        Assert.All(codex, x => Assert.Contains(x.Template.Agents, a => a.AgentId == "codex"));
        Assert.DoesNotContain(codex, x => x.Template.Id == "claude-local-gateway");
        var claude = TemplateCompatibilityService.Filter(all, new TemplateSelection { AgentId = "claude-code", AgentMode = AgentMode.ExperimentalLocalGateway }, Env());
        Assert.Single(claude);
        var small = TemplateCompatibilityService.Filter(all, new TemplateSelection { AgentId = "codex", Model = "gpt-oss:20b" }, Env(modelCtx: 16384));
        Assert.DoesNotContain(small, x => x.Template.MinimumContext > 16384);
    }
}

public class DeterministicConfigurationTests
{
    private static ConfigurationBuilderService Builder() => new([new CodexProvider(new NoProcessesFake()), new ClaudeCodeProvider(new NoProcessesFake())]);
    private static readonly IReadOnlyDictionary<string, string> Versions = new Dictionary<string, string> { ["codex"] = "0.161.0", ["ollama"] = "0.40.6" };

    private static GeneratedConfiguration Gen(string template, TemplateSelection s, IReadOnlyDictionary<string, string>? versions = null) =>
        Builder().Generate(new ConfigurationInputs(BuiltInTemplates.All.First(t => t.Id == template), s, versions ?? Versions, "sha256:abc"),
            BuiltInPermissionProfiles.Get(s.PermissionProfileId ?? BuiltInTemplates.All.First(t => t.Id == template).PermissionProfileId));

    [Fact]
    public void Same_inputs_give_the_same_configuration_and_fingerprint()
    {
        var s = new TemplateSelection { AgentId = "codex", Model = "gpt-oss:20b" };
        var a = Gen("code-review-local", s);
        var b = Gen("code-review-local", s);
        Assert.Equal(a.CanonicalJson, b.CanonicalJson);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.Equal(a.Stack.Id, b.Stack.Id);
        var reordered = Gen("code-review-local", s, new Dictionary<string, string> { ["ollama"] = "0.40.6", ["codex"] = "0.161.0" });
        Assert.Equal(a.Fingerprint, reordered.Fingerprint);
    }

    [Fact]
    public void Different_inputs_change_the_fingerprint()
    {
        var s = new TemplateSelection { AgentId = "codex", Model = "gpt-oss:20b" };
        var a = Gen("code-review-local", s);
        Assert.NotEqual(a.Fingerprint, Gen("code-review-local", s with { Model = "qwen3:14b" }).Fingerprint);
        Assert.NotEqual(a.Fingerprint, Gen("code-review-local", s, new Dictionary<string, string> { ["codex"] = "0.162.0", ["ollama"] = "0.40.6" }).Fingerprint);
        Assert.NotEqual(a.Fingerprint, Gen("code-review-local", s with { Reasoning = "low" }).Fingerprint);
    }

    [Fact]
    public void Generated_configuration_has_no_machine_paths_and_carries_template_options()
    {
        var g = Gen("code-review-local", new TemplateSelection { AgentId = "codex", Model = "gpt-oss:20b" });
        Assert.DoesNotContain(Environment.UserName, g.CanonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("model_reasoning_effort = \\u0022high\\u0022", g.CanonicalJson, StringComparison.Ordinal);
        Assert.Equal("read-only", g.Stack.PermissionProfileId);
        Assert.Equal("template:code-review-local", g.Stack.TemplateSource);
        Assert.Equal("1.0.0", g.Stack.TemplateVersion);
        Assert.Equal(g.Fingerprint, g.Stack.ConfigurationFingerprint);
    }

    [Fact]
    public void Reasoning_is_left_out_with_a_warning_for_a_model_without_thinking()
    {
        // Found live: Codex + llama3.2 rejected every request with "does not support thinking".
        var t = BuiltInTemplates.All.First(x => x.Id == "code-review-local");
        var s = new TemplateSelection { AgentId = "codex", Model = "llama3.2:latest" };
        var perms = BuiltInPermissionProfiles.Get(t.PermissionProfileId);
        var plain = Builder().Generate(new ConfigurationInputs(t, s, Versions, "sha256:abc", ModelCapabilities.Completion | ModelCapabilities.Tools), perms);
        Assert.False(plain.Stack.Options?.ContainsKey(TemplateOptions.Reasoning) == true);
        Assert.DoesNotContain("model_reasoning_effort", plain.CanonicalJson, StringComparison.Ordinal);
        Assert.Contains(plain.Warnings, w => w.Contains("does not support thinking", StringComparison.Ordinal));

        var thinking = Builder().Generate(new ConfigurationInputs(t, s, Versions, "sha256:abc", ModelCapabilities.Completion | ModelCapabilities.Tools | ModelCapabilities.Thinking), perms);
        Assert.Contains("model_reasoning_effort", thinking.CanonicalJson, StringComparison.Ordinal);
        Assert.DoesNotContain(thinking.Warnings, w => w.Contains("does not support thinking", StringComparison.Ordinal));
        Assert.NotEqual(plain.Fingerprint, thinking.Fingerprint);
    }
}
