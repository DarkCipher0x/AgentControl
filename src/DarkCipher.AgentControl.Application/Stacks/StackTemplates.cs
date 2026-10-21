using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Stacks;

namespace DarkCipher.AgentControl.Application.Stacks;

/// <summary>Built-in stack templates. Model names are defaults the user can change; nothing is installed by a template.</summary>
public static class StackTemplates
{
    public const string Ollama = "http://127.0.0.1:11434";

    public static IReadOnlyList<StackProfile> BuiltIn { get; } =
    [
        new()
        {
            Id = "codex-ollama-balanced", Name = "Codex + Ollama (Balanced)", BuiltIn = true,
            Description = "OpenAI Codex CLI with a local model through Ollama, in an isolated Codex profile. Fully local.",
            AgentProviderId = "codex", AgentMode = AgentMode.LocalProvider, RuntimeId = "ollama", Endpoint = Ollama,
            PrimaryModel = "gpt-oss:20b", UtilityModel = "qwen2.5-coder:7b", ContextLength = 32768,
            PermissionProfileId = BuiltInPermissionProfiles.AskBeforeChangesId, Privacy = PrivacyMode.FullyLocal,
        },
        new()
        {
            Id = "codex-ollama-lightweight", Name = "Codex + Ollama (Lightweight)", BuiltIn = true,
            Description = "Small, fast local model for modest hardware.",
            AgentProviderId = "codex", AgentMode = AgentMode.LocalProvider, RuntimeId = "ollama", Endpoint = Ollama,
            PrimaryModel = "qwen2.5-coder:7b", ContextLength = 16384,
            PermissionProfileId = BuiltInPermissionProfiles.AskBeforeChangesId, Privacy = PrivacyMode.FullyLocal,
        },
        new()
        {
            Id = "claude-native", Name = "Claude Code (Native)", BuiltIn = true,
            Description = "Claude Code with Anthropic's Claude models and your own login. Prompts and code are sent to Anthropic.",
            AgentProviderId = "claude-code", AgentMode = AgentMode.Native,
            PermissionProfileId = BuiltInPermissionProfiles.AskBeforeChangesId, Privacy = PrivacyMode.CloudAllowed,
        },
        new()
        {
            Id = "claude-local-gateway", Name = "Claude Code + Local Gateway (Experimental)", BuiltIn = true,
            Description = "Claude Code pointed at a local model through Ollama's Anthropic-compatible API. Not supported by Anthropic; the model is NOT Claude.",
            AgentProviderId = "claude-code", AgentMode = AgentMode.ExperimentalLocalGateway, RuntimeId = "ollama", Endpoint = Ollama,
            PrimaryModel = "gpt-oss-64k", ContextLength = 65536,
            PermissionProfileId = BuiltInPermissionProfiles.AskBeforeChangesId, Privacy = PrivacyMode.FullyLocal,
        },
        new()
        {
            Id = "offline-readonly", Name = "Offline Review (Read-Only)", BuiltIn = true,
            Description = "Fully local, read-only code review and Q&A. The agent cannot change files or run commands.",
            AgentProviderId = "codex", AgentMode = AgentMode.LocalProvider, RuntimeId = "ollama", Endpoint = Ollama,
            PrimaryModel = "qwen3:14b", ContextLength = 32768,
            PermissionProfileId = BuiltInPermissionProfiles.ReadOnlyId, Privacy = PrivacyMode.FullyLocal,
        },
        new()
        {
            Id = "hybrid-codex", Name = "Hybrid (Codex native, local fallback planned)", BuiltIn = true,
            Description = "Codex with OpenAI's models. Automatic local fallback is Planned and not performed.",
            AgentProviderId = "codex", AgentMode = AgentMode.Native,
            PermissionProfileId = BuiltInPermissionProfiles.AskBeforeChangesId, Privacy = PrivacyMode.Hybrid,
        },
    ];
}
