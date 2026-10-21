namespace DarkCipher.AgentControl.Domain.Verification;

/// <summary>
/// How a configuration is labelled. "Supported and Verified" is reserved for a combination that passed every requirement
/// AND a safe end-to-end test on this PC; everything short of that gets a weaker, honest label.
/// </summary>
public enum VerificationOutcome
{
    Detected,
    Available,
    CompatibleOnPaper,
    NotYetTested,
    ReadyWithLimitations,
    Experimental,
    Unsupported,
    FailedValidation,
    SupportedAndVerified,
}

/// <summary>The facts the classifier needs. Each flag must be PROVEN by a check that actually ran.</summary>
public sealed record VerificationEvidence
{
    public bool ComponentsInstalled { get; init; }
    public bool VersionsSupported { get; init; }
    public bool EndpointsReachable { get; init; }
    public bool ProtocolsMatch { get; init; }
    public bool ModelCapabilitiesVerified { get; init; }
    public bool AuthenticationValid { get; init; } = true;
    public bool ContextSatisfied { get; init; }
    public bool HardwareSufficient { get; init; }
    public bool ServicesHealthy { get; init; }
    public bool ConfigurationGenerated { get; init; }
    public bool ConfigurationValidated { get; init; }
    public bool EndToEndPassed { get; init; }
    /// <summary>The path is labelled experimental by its vendor or this app (e.g. Claude Code via a local gateway).</summary>
    public bool ExperimentalPath { get; init; }
    public bool EndToEndAttempted { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public static class VerificationClassifier
{
    /// <summary>The strict rule from the specification: every condition must be proven for Supported and Verified.</summary>
    public static VerificationOutcome Classify(VerificationEvidence e)
    {
        if (!e.ComponentsInstalled) return VerificationOutcome.Detected;
        var requirements = e.VersionsSupported && e.EndpointsReachable && e.ProtocolsMatch && e.AuthenticationValid && e.ServicesHealthy
                           && e.ConfigurationGenerated && e.ConfigurationValidated;
        if (!requirements) return VerificationOutcome.FailedValidation;
        if (!e.ModelCapabilitiesVerified) return VerificationOutcome.CompatibleOnPaper;
        if (!e.EndToEndAttempted) return VerificationOutcome.NotYetTested;
        if (!e.EndToEndPassed) return VerificationOutcome.FailedValidation;
        if (e.ExperimentalPath) return VerificationOutcome.Experimental;
        if (!e.ContextSatisfied || !e.HardwareSufficient || e.Limitations.Count > 0) return VerificationOutcome.ReadyWithLimitations;
        return VerificationOutcome.SupportedAndVerified;
    }

    public static string Label(VerificationOutcome o) => o switch
    {
        VerificationOutcome.SupportedAndVerified => "Supported and Verified",
        VerificationOutcome.ReadyWithLimitations => "Ready with Limitations",
        VerificationOutcome.CompatibleOnPaper => "Compatible on Paper",
        VerificationOutcome.NotYetTested => "Not Yet Tested",
        VerificationOutcome.FailedValidation => "Failed Validation",
        _ => o.ToString(),
    };
}

/// <summary>One row of the supported-configuration matrix: the precise combination that was verified, and when.</summary>
public sealed record SupportedConfigurationEntry
{
    public required string Label { get; init; }
    public required string OperatingSystem { get; init; }
    public required string AppVersion { get; init; }
    public required string Agent { get; init; }
    public string? AgentVersion { get; init; }
    public required string Runtime { get; init; }
    public string? RuntimeVersion { get; init; }
    public string? Gateway { get; init; }
    public string? GatewayVersion { get; init; }
    public required string Model { get; init; }
    public string? ModelDigest { get; init; }
    public required string Template { get; init; }
    public required string TemplateVersion { get; init; }
    public int? Context { get; init; }
    public required string ToolCalling { get; init; }
    public required string StructuredOutput { get; init; }
    public required string InstallResult { get; init; }
    public required string LaunchResult { get; init; }
    public required string EndToEndResult { get; init; }
    public DateTimeOffset VerifiedAt { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public VerificationOutcome Result { get; init; }
    public string? ConfigurationFingerprint { get; init; }
}
