using DarkCipher.AgentControl.Sdk;

namespace DarkCipher.AgentControl.Extensions;

public enum TrustLevel { BuiltIn, Verified, Signed, Community, Unsigned, Development, Blocked, Incompatible }

public enum LifecycleState { Discovered, Validated, Installed, Disabled, Loading, Ready, Degraded, Failed, Removed, Blocked, Incompatible }

/// <summary>Host-side view of one extension. Mutable state lives only in the host.</summary>
public sealed record ExtensionRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Publisher { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = "";
    public TrustLevel Trust { get; init; }
    public LifecycleState State { get; init; }
    public string StateReason { get; init; } = "";
    public bool Enabled { get; init; }
    public bool IsDeclarative { get; init; } = true;
    public IReadOnlyList<ExtensionType> Types { get; init; } = [];
    public IReadOnlyList<ExtensionPermission> RequestedPermissions { get; init; } = [];
    public IReadOnlyList<ExtensionPermission> GrantedPermissions { get; init; } = [];
    public IReadOnlyList<string> NetworkEndpoints { get; init; } = [];
    public string? License { get; init; }
    public string? Source { get; init; }
    public string? InstallPath { get; init; }
    public string SignatureStatus { get; init; } = "Unsigned";
    public int FailureCount { get; init; }
    public string Isolation { get; init; } = "";
}

/// <summary>What the pre-install review screen shows.</summary>
public sealed record InstallReview(
    ExtensionManifest Manifest,
    TrustLevel Trust,
    string SignatureStatus,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<(ExtensionPermission Permission, string Meaning, string Risk)> Permissions,
    string Isolation,
    string StagingPath)
{
    public bool CanInstall => Errors.Count == 0;
}

/// <summary>Application services the extension host needs. Implemented by the app's composition root so this assembly
/// stays independent of the Application layer.</summary>
public interface IExtensionHostServices
{
    void RegisterStackTemplate(string extensionId, SdkStackTemplate template);
    Task<string> SubmitModelDownloadAsync(string extensionId, string modelName, CancellationToken ct);
    Version AppVersion { get; }
    /// <summary>SDK 1.1: a schema-2 template (validated; throws CapabilityDeniedException when invalid).</summary>
    void RegisterTemplate(string extensionId, string templateJson);
    /// <summary>SDK 1.1: an Unverified agent-tool catalogue entry installed only by the host's winget path.</summary>
    void RegisterAgentTool(string extensionId, SdkAgentToolEntry tool);
}
