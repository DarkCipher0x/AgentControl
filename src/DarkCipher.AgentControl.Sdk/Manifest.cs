using System.Text.Json.Serialization;

namespace DarkCipher.AgentControl.Sdk;

/// <summary>SDK identity and compatibility. The host refuses extensions whose sdkVersion major differs or whose minor
/// is newer than the host's SDK.</summary>
public static class SdkInfo
{
    /// <summary>1.1 adds templates (schema 2), catalogue entries, model sources, compatibility rules, health checks and
    /// settings panels. Additive: every 1.0 extension still loads.</summary>
    public static readonly Version Version = new(1, 1, 0);
    public const string ManifestFileName = "extension.json";
    public const string IntegrityFileName = "files.sha256";
    public const string PackageExtension = ".dcxext";

    public static bool IsCompatible(Version extensionSdk) =>
        extensionSdk.Major == Version.Major && extensionSdk.Minor <= Version.Minor;
}

public enum ExtensionType
{
    AgentProvider,
    Runtime,
    ModelSource,
    HardwareDetector,
    Gateway,
    StackTemplate,
    RecommendationProvider,
    CompatibilityTester,
    ProjectIntegration,
    ObservabilityIntegration,
    DashboardPanel,
    Command,
    Importer,
    Exporter,
}

/// <summary>Least-privilege permissions an extension can request. Each is reviewed by the user before install and
/// checked by the capability broker on every call.</summary>
public enum ExtensionPermission
{
    Network,
    FileReads,
    ProjectReads,
    ProjectWrites,
    ProcessExecution,
    ModelManagement,
    RuntimeManagement,
    CredentialReferences,
    UI,
    Settings,
    Logging,
    Hardware,
    Sessions,
    /// <summary>SDK 1.1: contribute agent-tool catalogue entries, model sources and compatibility rules (data only; the host
    /// performs every install after the user's approval).</summary>
    CatalogueContributions,
}

public sealed record ExtensionDependency(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("minVersion")] string MinVersion);

public sealed record ExtensionContributions
{
    /// <summary>Relative paths (inside the package) of stack template JSON files.</summary>
    [JsonPropertyName("stackTemplates")] public List<string> StackTemplates { get; init; } = [];
    /// <summary>Relative paths of model catalogue JSON files (curated model lists).</summary>
    [JsonPropertyName("modelCatalogs")] public List<string> ModelCatalogs { get; init; } = [];
}

/// <summary>The extension manifest (extension.json). Schema: schemas/extension-manifest.schema.json.</summary>
public sealed record ExtensionManifest
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("publisher")] public string Publisher { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("minAppVersion")] public string MinAppVersion { get; init; } = "";
    [JsonPropertyName("maxTestedVersion")] public string MaxTestedVersion { get; init; } = "";
    [JsonPropertyName("sdkVersion")] public string SdkVersion { get; init; } = "";
    /// <summary>"Assembly.dll!Namespace.Type" for code extensions; null for declarative (data-only) extensions.</summary>
    [JsonPropertyName("entryPoint")] public string? EntryPoint { get; init; }
    [JsonPropertyName("types")] public List<ExtensionType> Types { get; init; } = [];
    [JsonPropertyName("permissions")] public List<ExtensionPermission> Permissions { get; init; } = [];
    [JsonPropertyName("requiredServices")] public List<string> RequiredServices { get; init; } = [];
    [JsonPropertyName("networkEndpoints")] public List<string> NetworkEndpoints { get; init; } = [];
    [JsonPropertyName("dependencies")] public List<ExtensionDependency> Dependencies { get; init; } = [];
    [JsonPropertyName("license")] public string License { get; init; } = "";
    [JsonPropertyName("source")] public string? Source { get; init; }
    [JsonPropertyName("signature")] public string? Signature { get; init; }
    [JsonPropertyName("contributes")] public ExtensionContributions Contributes { get; init; } = new();

    [JsonIgnore] public bool IsDeclarative => string.IsNullOrEmpty(EntryPoint);
}
