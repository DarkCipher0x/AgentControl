using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Domain.Tools;

public enum ToolKind { AgentTool, ModelRuntime, Gateway, SupportingTool }

/// <summary>Built-in providers are reviewed and their package identity is pinned. Custom and extension-provided tools are
/// Unverified until a local validation test passes, and only then may templates use them.</summary>
public enum ToolTrust { BuiltInVerified, ValidatedLocally, Unverified }

public enum ToolInstallState { Unknown, NotInstalled, Installed, UpdateAvailable, Broken }

public enum ToolAuthState { NotRequired, Unknown, Configured, Required }

public enum InstallOperation { Install, Update, Repair, Uninstall, Rollback }

/// <summary>Where a tool comes from. Only an official package manager with an exact, pinned package identity is accepted
/// for installs (today: winget's community repository, which checks the installer SHA-256 from the reviewed manifest).</summary>
public sealed record PackageSource(string Manager, string PackageId, string Repository, string Publisher, string Homepage, string Licence)
{
    public string Display => $"{Manager} '{PackageId}' from the '{Repository}' source (publisher {Publisher})";
}

/// <summary>Everything the catalogue shows about a tool, before anything is detected or installed.</summary>
public sealed record AgentToolDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Publisher { get; init; }
    public required string Description { get; init; }
    public ToolKind Kind { get; init; } = ToolKind.AgentTool;
    public ToolTrust Trust { get; init; } = ToolTrust.BuiltInVerified;
    public PackageSource? Source { get; init; }
    public required string InstallMethod { get; init; }
    public IReadOnlyList<string> SupportedOperatingSystems { get; init; } = ["Windows 10 22H2 or later (x64)", "Windows 11 (x64)"];
    public IReadOnlyList<string> Prerequisites { get; init; } = [];
    public IReadOnlyList<string> RequiredRuntimes { get; init; } = [];
    public string Authentication { get; init; } = "None";
    public IReadOnlyList<string> SupportedProtocols { get; init; } = [];
    public string SupportedModels { get; init; } = "";
    public IReadOnlyList<string> KnownLimitations { get; init; } = [];
    public SecurityClassification Security { get; init; } = SecurityClassification.Medium;
    /// <summary>The newest version this app has been verified with (catalogue data, dated in <see cref="CatalogueDate"/>).</summary>
    public string? LatestVerifiedVersion { get; init; }
    public string? MinimumSupportedVersion { get; init; }
    public string CatalogueDate { get; init; } = "2026-10-20";
    public long EstimatedDiskBytes { get; init; }
    public IReadOnlyList<string> AffectedLocations { get; init; } = [];
    public IReadOnlyList<string> ServicesStarted { get; init; } = [];
    public IReadOnlyList<string> EnvironmentChanges { get; init; } = [];
    public IReadOnlyList<string> ConfigurationChanges { get; init; } = [];
    public bool CanInstall { get; init; } = true;
    public bool CanUpdate { get; init; } = true;
    public bool CanUninstall { get; init; } = true;
    /// <summary>Set when this item ships inside another catalogue tool (a gateway built into a runtime): installing it
    /// installs that tool, and update/uninstall are managed there.</summary>
    public string? PartOfToolId { get; init; }
    /// <summary>True when this app can only install, update, detect and remove the tool: no session or stack provider uses
    /// it yet. Shown as a label so nothing implies more support than exists.</summary>
    public bool CatalogueOnly { get; init; }
    /// <summary>A loopback port the tool serves on when it is running (runtimes and gateways), for detection and display.</summary>
    public int? LocalPort { get; init; }
}

public sealed record ToolStatus(
    string ToolId,
    ToolInstallState State,
    string? Version,
    string? ExecutablePath,
    ToolAuthState Auth,
    ComponentHealth Health,
    string Summary,
    string? AvailableVersion = null)
{
    public bool Installed => State is ToolInstallState.Installed or ToolInstallState.UpdateAvailable or ToolInstallState.Broken;
}

public sealed record PrerequisiteCheck(string Name, bool Met, string Detail, string? Remedy = null);

/// <summary>
/// The installation plan shown before anything happens. It is fingerprinted: the queue re-plans immediately before
/// executing and refuses to run if the fresh plan differs from the one the user approved.
/// </summary>
public sealed record InstallPlan
{
    public required string ToolId { get; init; }
    public required InstallOperation Operation { get; init; }
    public required string DisplayName { get; init; }
    public required string Publisher { get; init; }
    public required string Source { get; init; }
    public required string Version { get; init; }
    public required string Method { get; init; }
    public IReadOnlyList<string> Downloads { get; init; } = [];
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public required string Privileges { get; init; }
    public IReadOnlyList<string> AffectedLocations { get; init; } = [];
    public IReadOnlyList<string> ConfigurationChanges { get; init; } = [];
    public IReadOnlyList<string> Services { get; init; } = [];
    public IReadOnlyList<string> EnvironmentChanges { get; init; } = [];
    public required string Authentication { get; init; }
    public long EstimatedDiskBytes { get; init; }
    public required string Licence { get; init; }
    public required string Rollback { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<PrerequisiteCheck> Prerequisites { get; init; } = [];
    /// <summary>The exact command that will run (display form; the launch uses an argument list, never a shell).</summary>
    public required string CommandPreview { get; init; }
    public SecurityClassification Risk { get; init; } = SecurityClassification.Medium;
    /// <summary>Version installed before this operation (used for update rollback).</summary>
    public string? PreviousVersion { get; init; }

    public bool CanProceed => Prerequisites.All(p => p.Met);

    /// <summary>Stable fingerprint of what will run and change (not of free text that may be reworded).</summary>
    public string Fingerprint => Hashing.Sha256Hex(string.Join("\n", ToolId, Operation, Source, Version, Method, CommandPreview, Privileges,
        string.Join("|", EnvironmentChanges), string.Join("|", AffectedLocations), PreviousVersion ?? ""))[..16];

    public IReadOnlyList<string> ApprovalDetails()
    {
        var d = new List<string>
        {
            $"Tool: {DisplayName} — publisher {Publisher}",
            $"Operation: {Operation} → version {Version}",
            $"Source: {Source}",
            $"Method: {Method}",
            $"Privileges: {Privileges}",
            $"Estimated disk use: {Bytes.Format(EstimatedDiskBytes)}",
            $"Licence: {Licence}",
            $"Authentication: {Authentication}",
            $"Rollback: {Rollback}",
            $"Command: {CommandPreview}",
        };
        d.AddRange(Downloads.Select(x => "Download: " + x));
        d.AddRange(Dependencies.Select(x => "Dependency: " + x));
        d.AddRange(AffectedLocations.Select(x => "Files: " + x));
        d.AddRange(ConfigurationChanges.Select(x => "Configuration: " + x));
        d.AddRange(Services.Select(x => "Services: " + x));
        d.AddRange(EnvironmentChanges.Select(x => "Environment: " + x));
        d.AddRange(Warnings.Select(x => "⚠ " + x));
        d.AddRange(Prerequisites.Where(p => !p.Met).Select(p => $"Missing prerequisite: {p.Name} — {p.Detail}"));
        return d;
    }
}

public sealed record InstallOutcome(bool Success, string Message, string? InstalledVersion = null, bool RolledBack = false, string? RollbackDetail = null, ToolStatus? After = null);

/// <summary>
/// Port: one agent tool, runtime or gateway (Claude Code, Codex, Ollama, …). Each tool has its own adapter; every external
/// process goes through IProcessExecutor (no shell, full path, validated location). Planning is side-effect free.
/// </summary>
public interface IAgentToolProvider
{
    AgentToolDescriptor Descriptor { get; }
    Task<ToolStatus> DetectAsync(CancellationToken ct);
    Task<IReadOnlyList<PrerequisiteCheck>> CheckPrerequisitesAsync(CancellationToken ct);
    /// <summary>Newest version the package source offers, or null when unknown (offline, no package manager).</summary>
    Task<string?> GetAvailableVersionAsync(CancellationToken ct);
    Task<InstallPlan> PlanAsync(InstallOperation operation, CancellationToken ct);
    Task<InstallOutcome> ExecuteAsync(InstallPlan plan, IProgress<DownloadProgress> progress, CancellationToken ct);
}

/// <summary>A tool the user registered (advanced). Starts Unverified; templates accept it only after its validation passes.</summary>
public sealed record CustomAgentDefinition
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Executable { get; init; }
    public IReadOnlyList<string> VersionArguments { get; init; } = ["--version"];
    /// <summary>Health check: the version command must exit 0 and print something that looks like a version.</summary>
    public string HealthCheck { get; init; } = "version-command";
    public IReadOnlyList<string> SupportedProtocols { get; init; } = [];
    public IReadOnlyList<string> ConfigurationLocations { get; init; } = [];
    public IReadOnlyList<string> LaunchArguments { get; init; } = [];
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string AuthenticationMethod { get; init; } = "None";
    public IReadOnlyList<string> CompatibleRuntimes { get; init; } = [];
    public IReadOnlyList<string> CompatibleModels { get; init; } = [];
    public IReadOnlyList<string> RequestedPermissions { get; init; } = [];
    public ToolTrust Trust { get; init; } = ToolTrust.Unverified;
    public string? ValidatedVersion { get; init; }
    public DateTimeOffset? ValidatedAt { get; init; }
}
