using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;

namespace DarkCipher.AgentControl.Domain.Templates;

public enum TemplateCategory
{
    GeneralCoding, AgenticCoding, CodeReview, BugInvestigation, LargeRepositoryAnalysis, Refactoring, Documentation, TestGeneration,
    PlanningAndArchitecture, FastLocalAssistance, PrivateOfflineCoding, HybridLocalAndCloud, Experimental,
}

public enum TemplateOrigin { BuiltIn, Extension, User, Imported }

/// <summary>"none": never switch models. "suggest": readiness offers the fallback model as a fix. Automatic switching is Planned.</summary>
public enum FallbackPolicy { None, Suggest }

public sealed record AgentRequirement(string AgentId, IReadOnlyList<AgentMode> Modes, string? MinimumVersion = null);

public sealed record RuntimeRequirement(string RuntimeId, string? MinimumVersion = null);

/// <summary>
/// A versioned, validated stack template (schema 2). Templates are data: they name requirements and choose among options
/// the providers already understand. They cannot carry executables, arbitrary arguments, secrets, paths or credentials —
/// <c>Options</c> are interpreted by typed builders, and every launch still goes through the session service's checks.
/// </summary>
public sealed record TemplateDefinition
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    /// <summary>Semantic version of the template itself (major.minor.patch).</summary>
    public required string Version { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public TemplateCategory Category { get; init; } = TemplateCategory.GeneralCoding;
    public TemplateOrigin Origin { get; init; } = TemplateOrigin.User;
    /// <summary>Extension id or "user"; set by the catalogue, not by the file.</summary>
    public string? Author { get; init; }
    /// <summary>For a user copy: the template it was duplicated from ("Reset to default" restores from it).</summary>
    public string? DerivedFrom { get; init; }

    public IReadOnlyList<AgentRequirement> Agents { get; init; } = [];
    public RuntimeRequirement? Runtime { get; init; }
    public IReadOnlyList<AgentProtocol> Protocols { get; init; } = [];
    public ModelCapabilities RequiredCapabilities { get; init; } = ModelCapabilities.Completion;
    public int MinimumContext { get; init; }
    public int RecommendedContext { get; init; }
    public bool RequiresToolCalling { get; init; }
    public bool RequiresStructuredOutput { get; init; }
    public IReadOnlyList<PrivacyMode> AllowedPrivacy { get; init; } = [PrivacyMode.FullyLocal, PrivacyMode.LocalPreferred, PrivacyMode.Hybrid, PrivacyMode.CloudAllowed];

    /// <summary>Default model per runtime (the user may choose another compatible model).</summary>
    public string? DefaultModel { get; init; }
    public string? FallbackModel { get; init; }
    public FallbackPolicy Fallback { get; init; } = FallbackPolicy.None;
    public required string PermissionProfileId { get; init; }
    /// <summary>Named options for typed builders (e.g. "reasoning" = "medium"). Unknown names are rejected by validation.</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();
    /// <summary>Extra instructions given to the agent through its supported mechanism; plain text, size-limited.</summary>
    public string? SystemInstructions { get; init; }
    public IReadOnlyList<string> ServiceDependencies { get; init; } = [];
    public IReadOnlyList<string> LaunchOrder { get; init; } = [];
    public IReadOnlyList<string> HealthChecks { get; init; } = [];
    public IReadOnlyList<string> ValidationTests { get; init; } = [];
    public IReadOnlyList<string> SupportedOperatingSystems { get; init; } = ["windows-x64"];
    public IReadOnlyList<string> KnownLimitations { get; init; } = [];
}

/// <summary>What the user picked in the template builder.</summary>
public sealed record TemplateSelection
{
    public required string AgentId { get; init; }
    public AgentMode? AgentMode { get; init; }
    public string? RuntimeId { get; init; }
    public string? Model { get; init; }
    public string? ProjectId { get; init; }
    public string? PermissionProfileId { get; init; }
    public int? ContextLength { get; init; }
    public string? Reasoning { get; init; }
    public PrivacyMode? Privacy { get; init; }
}

public enum CheckOutcome { Passed, Warning, Failed, NotApplicable }

/// <summary>Corrective actions the UI can offer next to a failed check.</summary>
public enum CorrectiveAction
{
    None, InstallAgent, UpdateAgent, InstallRuntime, StartRuntime, PullModel, ChooseCompatibleModel, IncreaseStorage, ReduceContext,
    ConfigureAuthentication, RepairGateway, ChangePermissionProfile, ChangePrivacyMode, RunCompatibilityTest, ChooseProject,
    ValidateCustomAgent, RollBackConfiguration, ApproveNetworkDestination,
}

public sealed record CompatibilityCheck(string Name, CheckOutcome Outcome, string Detail, CorrectiveAction Fix = CorrectiveAction.None);

public sealed record TemplateCompatibility(string TemplateId, string TemplateVersion, IReadOnlyList<CompatibilityCheck> Checks)
{
    public bool CanLaunch => Checks.All(c => c.Outcome != CheckOutcome.Failed);
    public bool HasWarnings => Checks.Any(c => c.Outcome == CheckOutcome.Warning);
    public string Summary => !CanLaunch ? $"Cannot launch: {Checks.Count(c => c.Outcome == CheckOutcome.Failed)} check(s) failed."
        : HasWarnings ? "Can launch, with limitations." : "All checks passed.";
}

public sealed record TemplateValidationIssue(string Field, string Problem);
