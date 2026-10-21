using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Network;

namespace DarkCipher.AgentControl.Domain.Stacks;

public enum StackReadiness
{
    Unknown,
    Ready,
    ReadyWithLimitations,
    SetupIncomplete,
    ServiceUnavailable,
    ModelMissing,
    AgentMissing,
    /// <summary>Kept for stored reports from 0.1.x; shown as "Configuration invalid".</summary>
    ConfigurationError,
    CompatibilityFailed,
    RuntimeMissing,
    AuthenticationRequired,
    ConfigurationInvalid,
    PermissionConflict,
    SecurityPolicyBlocked,
}

/// <summary>A complete agentic coding stack: agent + runtime + models + permissions. Contains no secrets, ever.</summary>
public sealed record StackProfile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public required string AgentProviderId { get; init; }
    public AgentMode AgentMode { get; init; }
    public string? RuntimeId { get; init; }
    public string? Endpoint { get; init; }
    public string? PrimaryModel { get; init; }
    public string? FallbackModel { get; init; }
    public string? UtilityModel { get; init; }
    public int? ContextLength { get; init; }
    public string PermissionProfileId { get; init; } = BuiltInPermissionProfiles.DefaultId;
    public PrivacyMode Privacy { get; init; } = PrivacyMode.FullyLocal;
    public bool BuiltIn { get; init; }
    public string? TemplateSource { get; init; }
    /// <summary>Version of the template this stack was generated from (null for hand-made stacks).</summary>
    public string? TemplateVersion { get; init; }
    /// <summary>Named template options interpreted by the agent's typed builder (e.g. reasoning = high).</summary>
    public IReadOnlyDictionary<string, string>? Options { get; init; }
    /// <summary>Extra instructions passed through the agent's supported mechanism (validated, size-limited).</summary>
    public string? SystemInstructions { get; init; }
    /// <summary>Fingerprint of the deterministic configuration this stack was generated with.</summary>
    public string? ConfigurationFingerprint { get; init; }
}

public enum CheckSeverity { Info, Warning, Error }

public sealed record ReadinessCheck(string Name, bool Passed, string Detail, CheckSeverity Severity = CheckSeverity.Error)
{
    /// <summary>The corrective action the UI offers for a failed check.</summary>
    public Templates.CorrectiveAction Fix { get; init; }
    /// <summary>Evidence behind the result (what was measured or read), for Diagnose / View Details.</summary>
    public string? Evidence { get; init; }
}

public sealed record StackReadinessReport(string StackId, StackReadiness Readiness, IReadOnlyList<ReadinessCheck> Checks, DateTimeOffset CheckedAt)
{
    public string Summary => Label(Readiness);

    public static string Label(StackReadiness r) => r switch
    {
        StackReadiness.Ready => "Ready",
        StackReadiness.ReadyWithLimitations => "Ready with limitations",
        StackReadiness.SetupIncomplete => "Setup incomplete",
        StackReadiness.ServiceUnavailable => "Service unavailable",
        StackReadiness.ModelMissing => "Model missing",
        StackReadiness.AgentMissing => "Agent missing",
        StackReadiness.ConfigurationError or StackReadiness.ConfigurationInvalid => "Configuration invalid",
        StackReadiness.CompatibilityFailed => "Compatibility failed",
        StackReadiness.RuntimeMissing => "Runtime missing",
        StackReadiness.AuthenticationRequired => "Authentication required",
        StackReadiness.PermissionConflict => "Permission conflict",
        StackReadiness.SecurityPolicyBlocked => "Security policy blocked",
        _ => "Not checked",
    };
}

/// <summary>Port: checks a stack's readiness without changing anything.</summary>
public interface IStackValidator
{
    Task<StackReadinessReport> ValidateAsync(StackProfile stack, CancellationToken ct);
}
