using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.Application.Templates;

/// <summary>
/// Built-in templates (schema 2, version 1.0.0 each). They only combine what the providers already support; default
/// models are suggestions the user can change to any compatible installed model. Nothing here installs anything.
/// </summary>
public static class BuiltInTemplates
{
    private static readonly AgentRequirement CodexLocal = new("codex", [AgentMode.LocalProvider]);
    private static readonly AgentRequirement ClaudeGateway = new("claude-code", [AgentMode.VerifiedLocalGateway, AgentMode.ExperimentalLocalGateway], "2.1.0");
    private static readonly RuntimeRequirement Ollama = new("ollama", "0.9.0");
    private static readonly IReadOnlyList<PrivacyMode> LocalOnly = [PrivacyMode.FullyLocal, PrivacyMode.LocalPreferred, PrivacyMode.Hybrid, PrivacyMode.CloudAllowed];
    private static readonly IReadOnlyList<PrivacyMode> CloudOnly = [PrivacyMode.Hybrid, PrivacyMode.CloudAllowed];
    private static readonly IReadOnlyList<string> LocalTests = ["runtime-reachable", "model-installed", "basic-inference", "tool-call", "structured-output", "agent-version"];
    private const ModelCapabilities ChatTools = ModelCapabilities.Completion | ModelCapabilities.Tools;

    private static TemplateDefinition Local(string id, string name, TemplateCategory cat, string description, string model, int min, int rec, string permission,
        string? reasoning = null, string? instructions = null, IReadOnlyList<string>? limits = null) => new()
    {
        Id = id, Version = "1.0.0", DisplayName = name, Category = cat, Origin = TemplateOrigin.BuiltIn, Author = "DarkCipher Systems", Description = description,
        Agents = [CodexLocal], Runtime = Ollama, Protocols = [AgentProtocol.OllamaNative], RequiredCapabilities = ChatTools,
        MinimumContext = min, RecommendedContext = rec, RequiresToolCalling = true, RequiresStructuredOutput = true, AllowedPrivacy = LocalOnly,
        DefaultModel = model, PermissionProfileId = permission,
        Options = reasoning is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [TemplateOptions.Reasoning] = reasoning },
        SystemInstructions = instructions, ServiceDependencies = ["ollama"], LaunchOrder = ["ollama", "agent"], HealthChecks = ["runtime-reachable", "agent-version"],
        ValidationTests = LocalTests, KnownLimitations = limits ?? ["Codex's Windows sandbox is newer than on macOS/Linux; this app enforces the launch folder."],
    };

    public static IReadOnlyList<TemplateDefinition> All { get; } =
    [
        Local("general-coding-local", "General Coding (local)", TemplateCategory.GeneralCoding,
            "Everyday coding with Codex and a local model. Every change needs your approval.", "gpt-oss:20b", 32768, 65536, BuiltInPermissionProfiles.AskBeforeChangesId),
        Local("agentic-coding-local", "Agentic Coding (local)", TemplateCategory.AgenticCoding,
            "Multi-step work inside the project folder: edits are made directly, commands still ask.", "qwen3-coder:30b", 32768, 65536, BuiltInPermissionProfiles.WorkspaceOnlyId, "medium"),
        Local("code-review-local", "Code Review (read-only)", TemplateCategory.CodeReview,
            "Reads the code and reports findings. The agent cannot change files or run commands.", "gpt-oss:20b", 32768, 65536, BuiltInPermissionProfiles.ReadOnlyId, "high"),
        Local("bug-investigation-local", "Bug Investigation", TemplateCategory.BugInvestigation,
            "Trace a defect to its cause; proposes a fix and asks before changing anything.", "gpt-oss:20b", 32768, 65536, BuiltInPermissionProfiles.AskBeforeChangesId, "high"),
        Local("large-repo-analysis-local", "Large Repository Analysis", TemplateCategory.LargeRepositoryAnalysis,
            "Read-only analysis that needs a long context window.", "qwen3-coder:30b", 65536, 131072, BuiltInPermissionProfiles.ReadOnlyId, "medium",
            limits: ["Needs a model and GPU that can hold 64K+ tokens of context; smaller setups are refused rather than truncated silently."]),
        Local("refactoring-local", "Refactoring", TemplateCategory.Refactoring,
            "Structured refactoring with every edit approved.", "qwen3-coder:30b", 32768, 65536, BuiltInPermissionProfiles.AskBeforeChangesId, "medium"),
        Local("documentation-local", "Documentation", TemplateCategory.Documentation,
            "Write and update documentation; asks before changing files.", "qwen3:14b", 16384, 32768, BuiltInPermissionProfiles.AskBeforeChangesId, "low"),
        Local("test-generation-local", "Test Generation", TemplateCategory.TestGeneration,
            "Write tests for existing code; asks before changing files.", "qwen2.5-coder:14b", 16384, 32768, BuiltInPermissionProfiles.AskBeforeChangesId, "medium"),
        Local("planning-architecture-local", "Planning and Architecture", TemplateCategory.PlanningAndArchitecture,
            "Read-only design discussion and planning with high reasoning effort.", "gpt-oss:20b", 32768, 65536, BuiltInPermissionProfiles.ReadOnlyId, "high"),
        Local("fast-local-assist", "Fast Local Assistance", TemplateCategory.FastLocalAssistance,
            "A small, fast model for quick questions on modest hardware.", "qwen2.5-coder:7b", 8192, 16384, BuiltInPermissionProfiles.AskBeforeChangesId, "low",
            limits: ["Small models make more tool-calling mistakes; use a larger template for multi-step work."]),
        Local("private-offline-coding", "Private Offline Coding", TemplateCategory.PrivateOfflineCoding,
            "Fully local only: refused unless the app is in Fully Local or Local Preferred mode.", "gpt-oss:20b", 32768, 65536, BuiltInPermissionProfiles.AskBeforeChangesId) with
            { AllowedPrivacy = [PrivacyMode.FullyLocal, PrivacyMode.LocalPreferred] },
        new()
        {
            Id = "claude-native-hybrid", Version = "1.0.0", DisplayName = "Hybrid: Claude native, local fallback suggested", Category = TemplateCategory.HybridLocalAndCloud,
            Origin = TemplateOrigin.BuiltIn, Author = "DarkCipher Systems",
            Description = "Claude Code with Anthropic's models (prompts leave this PC). If it is unavailable, readiness suggests a local template; switching is never automatic.",
            Agents = [new AgentRequirement("claude-code", [AgentMode.Native], "2.1.0")], Protocols = [AgentProtocol.ProviderNative],
            RequiredCapabilities = ModelCapabilities.None, AllowedPrivacy = CloudOnly, PermissionProfileId = BuiltInPermissionProfiles.AskBeforeChangesId,
            Fallback = FallbackPolicy.Suggest, FallbackModel = "gpt-oss:20b", HealthChecks = ["agent-version"], ValidationTests = ["agent-version"],
            KnownLimitations = ["Needs your Claude account or API key (Claude Code asks on first launch).", "Automatic fallback is Planned; today the fallback is a suggestion only."],
        },
        new()
        {
            Id = "claude-local-gateway", Version = "1.0.0", DisplayName = "Claude Code + local gateway (Experimental)", Category = TemplateCategory.Experimental,
            Origin = TemplateOrigin.BuiltIn, Author = "DarkCipher Systems",
            Description = "Claude Code pointed at a local model through Ollama's Anthropic-compatible API. Not supported by Anthropic; the model is NOT Claude.",
            Agents = [ClaudeGateway], Runtime = Ollama, Protocols = [AgentProtocol.AnthropicMessages], RequiredCapabilities = ChatTools,
            MinimumContext = 65536, RecommendedContext = 65536, RequiresToolCalling = true, RequiresStructuredOutput = false, AllowedPrivacy = LocalOnly,
            DefaultModel = "gpt-oss:20b", PermissionProfileId = BuiltInPermissionProfiles.AskBeforeChangesId,
            ServiceDependencies = ["ollama"], LaunchOrder = ["ollama", "agent"], HealthChecks = ["runtime-reachable", "gateway", "agent-version"],
            ValidationTests = ["runtime-reachable", "model-installed", "basic-inference", "tool-call", "gateway", "agent-version"],
            KnownLimitations = ["Experimental: behaviour can change with any Claude Code update.", "Ollama recommends 64K tokens of context for Claude Code."],
        },
    ];
}
