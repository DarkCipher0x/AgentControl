namespace DarkCipher.AgentControl.Sdk;

/// <summary>Entry point implemented by a code extension. Called once after capability negotiation.</summary>
public interface IExtension
{
    /// <summary>Register contributions through <paramref name="context"/>. Must complete within the host's timeout
    /// (10 s) and must not block. Throwing marks the extension Failed; repeated failures auto-disable it.</summary>
    Task InitializeAsync(IExtensionContext context, CancellationToken cancellationToken);
}

public interface IExtensionContext
{
    string ExtensionId { get; }
    Version HostVersion { get; }
    Version HostSdkVersion { get; }
    /// <summary>Permissions the user granted (may be fewer than requested).</summary>
    IReadOnlySet<ExtensionPermission> GrantedPermissions { get; }
    ISdkCapabilityBroker Broker { get; }
}

public enum SdkLogLevel { Debug, Information, Warning, Error }

public enum SdkHealth { Healthy, Degraded, Unavailable, Failed }

/// <summary>Thrown when an extension calls a capability it was not granted, or the security policy refuses it.</summary>
public sealed class CapabilityDeniedException(string message) : Exception(message);

/// <summary>Opaque reference to a credential the user chose to share with the extension. Holds no secret.</summary>
public sealed record SdkCredentialHandle(string Id, string DisplayName);

public sealed record SdkModelInfo(string Name, string? Family, string? ParameterSize, string? Quantization, long? SizeBytes, int? ContextLength, IReadOnlyList<string> Capabilities);

public sealed record SdkProcessRequest(string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory, TimeSpan Timeout, string Purpose);

public sealed record SdkProcessResult(bool Succeeded, int? ExitCode, string StdOut, string StdErr, string Message);

public sealed record SdkStackTemplate(
    string Id,
    string Name,
    string Description,
    string AgentProviderId,
    string AgentMode,
    string? RuntimeId,
    string? Endpoint,
    string? PrimaryModel,
    string? UtilityModel,
    int? ContextLength);

public sealed record SdkCommand(string Id, string Title, string Description, Func<CancellationToken, Task<string>> Execute);

/// <summary>A dashboard panel described as data (title + rows). UI technology independent.</summary>
public sealed record SdkDashboardPanel(string Id, string Title, Func<CancellationToken, Task<IReadOnlyList<KeyValuePair<string, string>>>> GetRows);

// ---- SDK 1.1 contributions (additive) -----------------------------------------------------------------------------

/// <summary>An agent tool for the catalogue. Data only: the HOST installs it with winget using exactly this package id,
/// after showing the user the full plan. It is listed as "Unverified (extension)" and an extension can never run an
/// installer itself. Requires the CatalogueContributions permission.</summary>
public sealed record SdkAgentToolEntry(
    string Id,
    string DisplayName,
    string Publisher,
    string Description,
    string WingetPackageId,
    string Homepage,
    string Licence,
    IReadOnlyList<string> Protocols);

public sealed record SdkModelSearchResult(string Id, string DisplayName, string? Author, string? Licence, bool Gated, IReadOnlyList<string> Tags, string SourceUrl);

/// <summary>A downloadable model file. Url must be https; Sha256 is required for anything the host would download.</summary>
public sealed record SdkModelFile(string Name, long? SizeBytes, string? Sha256, string? Format, string? Quantization);

public sealed record SdkModelDetails(
    SdkModelSearchResult Entry,
    IReadOnlyList<SdkModelFile> Files,
    string? CardSummary,
    string? Architecture,
    int? ContextLength,
    bool RequiresAuthentication,
    bool RequiresCustomCode);

/// <summary>A model catalogue contributed by an extension (search and metadata only; the host decides how, or whether,
/// anything is acquired). Calls are time-limited and rate-limited by the host. Requires CatalogueContributions + Network.</summary>
public interface ISdkModelSource
{
    string Id { get; }
    string DisplayName { get; }
    Task<IReadOnlyList<SdkModelSearchResult>> SearchAsync(string query, int limit, CancellationToken ct);
    Task<SdkModelDetails?> GetDetailsAsync(string id, CancellationToken ct);
}

/// <summary>A declarative compatibility rule evaluated by the host's template checks (never code).
/// AgentId/ModelPattern use '*' wildcards; Effect is "warn" or "block".</summary>
public sealed record SdkCompatibilityRule(string Id, string Description, string? AgentId, string? ModelPattern, string Effect, string Message);

/// <summary>A health check shown on the Services page; run by the host with a timeout and isolated exceptions.</summary>
public sealed record SdkHealthCheck(string Id, string Title, Func<CancellationToken, Task<SdkHealthResult>> Run);

public sealed record SdkHealthResult(SdkHealth Health, string Detail);

public enum SdkSettingKind { Text, Toggle, Choice }

public sealed record SdkSettingField(string Key, string Label, SdkSettingKind Kind, IReadOnlyList<string>? Choices = null, string? Default = null, string? Help = null);

/// <summary>A settings panel described as data; values live in the extension's own scoped settings (Settings permission).</summary>
public sealed record SdkSettingsPanel(string Id, string Title, IReadOnlyList<SdkSettingField> Fields);

/// <summary>
/// Everything an extension may do goes through this broker. Each call is checked against the extension's granted
/// permissions and the application security policy, audited, isolated (exceptions and timeouts contained) and never
/// exposes internal services, database entities or secrets.
/// </summary>
public interface ISdkCapabilityBroker
{
    // Registration (UI / Settings permissions as noted)
    void RegisterStackTemplate(SdkStackTemplate template);                       // no permission needed (data only)
    void RegisterCommand(SdkCommand command);                                    // UI
    void RegisterDashboardPanel(SdkDashboardPanel panel);                        // UI

    // Information
    Task<IReadOnlyList<SdkModelInfo>> GetInstalledModelsAsync(CancellationToken ct);  // ModelManagement
    Task<IReadOnlyDictionary<string, string>> GetHardwareSummaryAsync(CancellationToken ct); // Hardware

    // Actions
    Task<SdkProcessResult> RunApprovedProcessAsync(SdkProcessRequest request, CancellationToken ct); // ProcessExecution
    Task<string> SubmitModelDownloadAsync(string modelName, CancellationToken ct);  // ModelManagement
    SdkCredentialHandle? GetCredentialReference(string name);                      // CredentialReferences

    // Extension-scoped settings (Settings) — stored per extension, never shared
    string? GetSetting(string key);
    void SetSetting(string key, string value);

    // Observability (Logging) — redacted by the host
    void Log(SdkLogLevel level, string message);
    void ReportHealth(SdkHealth health, string summary);

    // ---- SDK 1.1 ----
    /// <summary>A schema-2 template as JSON (validated by the host; permissions are always reset to Ask Before Changes).</summary>
    void RegisterTemplate(string templateJson);                                   // no permission needed (data only)
    void RegisterAgentTool(SdkAgentToolEntry tool);                               // CatalogueContributions
    void RegisterModelSource(ISdkModelSource source);                             // CatalogueContributions + Network
    void RegisterCompatibilityRule(SdkCompatibilityRule rule);                    // CatalogueContributions
    void RegisterHealthCheck(SdkHealthCheck check);                               // UI
    void RegisterSettingsPanel(SdkSettingsPanel panel);                           // Settings
}
