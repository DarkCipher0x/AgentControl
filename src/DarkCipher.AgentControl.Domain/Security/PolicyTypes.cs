using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;

namespace DarkCipher.AgentControl.Domain.Security;

/// <summary>Application-wide security policy. Secure defaults: fully local, no remote runtimes, no third-party code.</summary>
public sealed record SecurityPolicy
{
    public PrivacyMode Privacy { get; init; } = PrivacyMode.FullyLocal;
    public bool DevelopmentMode { get; init; }
    public bool DisableThirdPartyExtensions { get; init; } = true;
    public bool SafeMode { get; init; }
    public bool AllowRemoteRuntimes { get; init; }
    public bool RequireHttpsForRemote { get; init; } = true;
    /// <summary>Endpoints (scheme://host:port) the user explicitly approved for non-loopback traffic.</summary>
    public IReadOnlyList<string> ApprovedEndpoints { get; init; } = [];
}

public interface ISecurityPolicyService
{
    SecurityPolicy Current { get; }
    event EventHandler<SecurityPolicy>? Changed;
    /// <summary>Applies a change, persists it and audits it.</summary>
    OperationResult Update(Func<SecurityPolicy, SecurityPolicy> change, string reason);
    bool IsEndpointApproved(Uri uri);
}

/// <summary>A previewed change to a file the app does not own (agent configs, etc.).</summary>
public sealed record ConfigChangePreview(string TargetPath, bool TargetExists, string? CurrentContent, string NewContent, IReadOnlyList<string> DiffLines);

public sealed record ConfigBackup(string TargetPath, string BackupPath, DateTimeOffset CreatedAt, long SizeBytes);

/// <summary>Port: back up, preview, atomically apply and roll back changes to configuration files.</summary>
public interface IConfigurationProtection
{
    ConfigChangePreview Preview(string targetPath, string newContent);
    OperationResult<ConfigBackup?> Apply(ConfigChangePreview preview, CorrelationId correlation);
    OperationResult Rollback(ConfigBackup backup, CorrelationId correlation);
    IReadOnlyList<ConfigBackup> Backups(string targetPath);
    /// <summary>Removes a file this app created (used to roll back a transaction that created a new file). The file is
    /// backed up first and protected-path rules apply.</summary>
    OperationResult RemoveCreated(string targetPath, string expectedContent, CorrelationId correlation);
    /// <summary>Reads a configuration file (size-limited, protected-path rules apply). Null when it does not exist.</summary>
    string? Read(string targetPath);
}
