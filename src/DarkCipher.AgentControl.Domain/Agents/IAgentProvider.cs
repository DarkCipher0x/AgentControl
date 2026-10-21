using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Processes;

namespace DarkCipher.AgentControl.Domain.Agents;

/// <summary>How an agent reaches its model. Labels are shown verbatim; nothing local is ever called "Claude".</summary>
public enum AgentMode
{
    /// <summary>The vendor's own service (e.g. Claude Code with Anthropic's API).</summary>
    Native,
    /// <summary>A local gateway this app has verified with the compatibility suite.</summary>
    VerifiedLocalGateway,
    /// <summary>A local gateway that works in practice but is not supported by the agent vendor.</summary>
    ExperimentalLocalGateway,
    /// <summary>The agent's documented local/OSS provider mode (e.g. Codex --oss with Ollama).</summary>
    LocalProvider,
    Unsupported,
}

public enum AgentProtocol { OllamaNative, OpenAIChatCompletions, OpenAIResponses, AnthropicMessages, ProviderNative }

public sealed record AgentModeInfo(AgentMode Mode, string Label, FeatureMaturity Maturity, AgentProtocol Protocol, string Explanation);

public sealed record AgentInstallation(
    string ProviderId,
    bool Installed,
    string? ExecutablePath,
    string? Version,
    ComponentHealth Health,
    string Summary,
    IReadOnlyList<string> Notes);

/// <summary>A file the launch plan will create or change (always inside an app-managed directory).</summary>
public sealed record PlannedFileChange(string Path, string NewContent, string Reason);

public sealed record AgentLaunchRequest
{
    public required AgentInstallation Installation { get; init; }
    public required AgentMode Mode { get; init; }
    public required string ProjectDirectory { get; init; }
    public required PermissionProfile Permissions { get; init; }
    public string? Model { get; init; }
    public Uri? Endpoint { get; init; }
    public int? ContextLength { get; init; }
    /// <summary>App-managed directory for isolated agent profiles (e.g. a dedicated CODEX_HOME).</summary>
    public string? ProfileDirectory { get; init; }
    /// <summary>Template options the provider understands (unknown names are ignored with a warning).</summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
    public string? SystemInstructions { get; init; }
}

/// <summary>Option names and values templates may use. Anything else is rejected by template validation.</summary>
public static class TemplateOptions
{
    public const string Reasoning = "reasoning";
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Allowed = new Dictionary<string, IReadOnlyList<string>>
    {
        [Reasoning] = ["low", "medium", "high"],
    };
    public const int MaxSystemInstructions = 4000;
}

public sealed record AgentLaunchPlan(
    ProcessSpec Process,
    AgentMode Mode,
    string BackendDescription,
    FeatureMaturity Maturity,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<PlannedFileChange> FileChanges,
    IReadOnlyList<PermissionControl> PermissionControls);

/// <summary>Port: an agentic coding tool (Claude Code, Codex, future CLIs). Launch-plan building is pure and testable;
/// the actual launch always goes through IProcessExecutor after protected-path and policy checks.</summary>
public interface IAgentProvider
{
    string Id { get; }
    string DisplayName { get; }
    string Vendor { get; }
    FeatureMaturity Maturity { get; }
    IReadOnlyList<AgentModeInfo> Modes { get; }

    Task<AgentInstallation> DetectAsync(CancellationToken ct);
    /// <summary>Forget any cached detection result (Rescan).</summary>
    void InvalidateDetection();
    AgentLaunchPlan BuildLaunchPlan(AgentLaunchRequest request);
    IReadOnlyList<PermissionControl> DescribeEnforcement(PermissionProfile profile, AgentMode mode);
}

