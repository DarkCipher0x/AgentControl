using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Common;

namespace DarkCipher.AgentControl.Integrations.Tools;

/// <summary>
/// Shared behaviour for tools installed with winget. Each tool supplies its descriptor (pinned package identity and the
/// facts shown in the plan) and its own detection; installation, update, repair, uninstall and rollback are planned here
/// and executed through <see cref="WingetClient"/>. Nothing runs without a plan, and repair never reinstalls a healthy tool.
/// </summary>
public abstract class WingetToolProvider : IAgentToolProvider
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    protected WingetClient Winget { get; }

    protected WingetToolProvider(WingetClient winget) => Winget = winget;

    public abstract AgentToolDescriptor Descriptor { get; }
    protected PackageSource Package => Descriptor.Source ?? throw new InvalidOperationException("No package source.");

    public abstract Task<ToolStatus> DetectAsync(CancellationToken ct);

    /// <summary>Forget cached detection so the next DetectAsync sees the new state.</summary>
    protected virtual void Invalidate() { }

    public virtual Task<IReadOnlyList<PrerequisiteCheck>> CheckPrerequisitesAsync(CancellationToken ct)
    {
        var winget = Winget.ExecutablePath;
        IReadOnlyList<PrerequisiteCheck> checks =
        [
            new("Windows x64", Environment.Is64BitOperatingSystem && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041),
                Environment.Is64BitOperatingSystem ? "64-bit Windows " + Environment.OSVersion.Version : "A 64-bit Windows 10 (2004+) or 11 is required."),
            new("Windows Package Manager (winget)", winget is not null, winget is not null ? "winget is available." : "App Installer (winget) is missing.",
                "Install or update 'App Installer' from the Microsoft Store, then rescan."),
            new("Free disk space", FreeBytes() is not { } free || free > Descriptor.EstimatedDiskBytes * 2,
                FreeBytes() is { } f ? $"{Bytes.Format(f)} free on the system drive." : "Free space unknown.", "Free some disk space."),
        ];
        return Task.FromResult(checks);
    }

    private static long? FreeBytes()
    {
        try { return new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    public virtual Task<string?> GetAvailableVersionAsync(CancellationToken ct) => Winget.AvailableVersionAsync(Package.PackageId, ct);

    public async Task<InstallPlan> PlanAsync(InstallOperation operation, CancellationToken ct)
    {
        var d = Descriptor;
        var status = await DetectAsync(ct).ConfigureAwait(false);
        var prereq = await CheckPrerequisitesAsync(ct).ConfigureAwait(false);
        var warnings = new List<string>();
        var prerequisites = prereq.ToList();
        var exe = Winget.ExecutablePath ?? "winget.exe";
        string version, rollback, privileges = "Your user account only (per-user install; no administrator rights, no UAC prompt).";
        IReadOnlyList<string> args;
        string? previous = status.Version;

        switch (operation)
        {
            case InstallOperation.Install:
                if (!d.CanInstall) throw new InvalidOperationException($"{d.DisplayName} cannot be installed by this app: {d.InstallMethod}");
                if (status.Installed) prerequisites.Add(new("Not already installed", false, $"{d.DisplayName} {status.Version} is already installed.", "Use Update or Repair instead."));
                version = await GetAvailableVersionAsync(ct).ConfigureAwait(false) ?? "latest from the winget source";
                args = WingetClient.InstallArguments(Package.PackageId, null);
                rollback = "Uninstall with winget (removes the program; your settings folders are kept).";
                break;
            case InstallOperation.Update:
                if (!status.Installed) prerequisites.Add(new("Installed", false, $"{d.DisplayName} is not installed.", "Install it first."));
                version = await GetAvailableVersionAsync(ct).ConfigureAwait(false) ?? "latest from the winget source";
                if (status.Version is not null && VersionText.Compare(version, status.Version) <= 0)
                    prerequisites.Add(new("Newer version available", false, $"{status.Version} is the newest version the source offers.", null));
                args = WingetClient.UpgradeArguments(Package.PackageId);
                rollback = previous is null ? "Not available (the current version is unknown)."
                    : $"Reinstall the previous version {VersionText.Parse(previous)?.ToString(3) ?? previous} with winget, if the source still offers it.";
                break;
            case InstallOperation.Repair:
                var diagnosis = Diagnose(status);
                if (diagnosis is null) prerequisites.Add(new("Something to repair", false, $"{d.DisplayName} looks healthy: {status.Summary}", "Nothing to do."));
                else warnings.Add("Diagnosis: " + diagnosis);
                version = VersionText.Parse(status.Version)?.ToString(3) ?? await GetAvailableVersionAsync(ct).ConfigureAwait(false) ?? "latest";
                args = WingetClient.InstallArguments(Package.PackageId, WingetClient.IsValidVersion(version) ? version : null, force: true);
                rollback = "Not needed: the same version is reinstalled over the broken one.";
                break;
            case InstallOperation.Uninstall:
            case InstallOperation.Rollback:
                if (!d.CanUninstall) throw new InvalidOperationException($"{d.DisplayName} cannot be uninstalled from here.");
                if (!status.Installed) prerequisites.Add(new("Installed", false, $"{d.DisplayName} is not installed.", null));
                version = status.Version ?? "installed version";
                args = WingetClient.UninstallArguments(Package.PackageId);
                rollback = "Install it again from the catalogue.";
                warnings.Add("Stacks that use this tool will report it as missing until it is installed again.");
                break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }

        return new InstallPlan
        {
            ToolId = d.Id,
            Operation = operation,
            DisplayName = d.DisplayName,
            Publisher = d.Publisher,
            Source = Package.Display,
            Version = version,
            Method = d.InstallMethod,
            Downloads = operation is InstallOperation.Uninstall or InstallOperation.Rollback
                ? Array.Empty<string>()
                : new[] { $"The installer named in winget's reviewed manifest for {Package.PackageId} (SHA-256 checked by winget)" },
            Dependencies = d.RequiredRuntimes,
            Privileges = privileges,
            AffectedLocations = d.AffectedLocations,
            ConfigurationChanges = operation == InstallOperation.Install ? d.ConfigurationChanges : [],
            Services = d.ServicesStarted,
            EnvironmentChanges = operation == InstallOperation.Install ? d.EnvironmentChanges : [],
            Authentication = d.Authentication,
            EstimatedDiskBytes = d.EstimatedDiskBytes,
            Licence = d.Source?.Licence ?? "Unknown",
            Rollback = rollback,
            Warnings = warnings.Concat(d.KnownLimitations.Select(k => "Limitation: " + k)).ToList(),
            Prerequisites = prerequisites,
            CommandPreview = Domain.Processes.ProcessPreview.Render(exe, args),
            Risk = operation is InstallOperation.Uninstall ? SecurityClassification.Medium : d.Security,
            PreviousVersion = previous,
        };
    }

    /// <summary>Why the tool needs repair, or null when it is healthy (repair is never offered for a healthy tool).</summary>
    public static string? Diagnose(ToolStatus s) => s switch
    {
        { State: ToolInstallState.NotInstalled } => null,
        { State: ToolInstallState.Broken } => s.Summary,
        { Health: ComponentHealth.Degraded or ComponentHealth.Crashed or ComponentHealth.Misconfigured } => s.Summary,
        { Installed: true, Version: null } => "Installed but did not report a version.",
        _ => null,
    };

    public async Task<InstallOutcome> ExecuteAsync(InstallPlan plan, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (plan.ToolId != Descriptor.Id) return new InstallOutcome(false, "The plan belongs to another tool.");
        if (!plan.CanProceed) return new InstallOutcome(false, "A prerequisite is not met: " + string.Join("; ", plan.Prerequisites.Where(p => !p.Met).Select(p => p.Detail)));

        IReadOnlyList<string> args = plan.Operation switch
        {
            InstallOperation.Install => WingetClient.InstallArguments(Package.PackageId, null),
            InstallOperation.Update => WingetClient.UpgradeArguments(Package.PackageId),
            InstallOperation.Repair => WingetClient.InstallArguments(Package.PackageId, WingetClient.IsValidVersion(plan.Version) ? plan.Version : null, force: true),
            _ => WingetClient.UninstallArguments(Package.PackageId),
        };
        var verb = plan.Operation switch { InstallOperation.Install => "Installing", InstallOperation.Update => "Updating", InstallOperation.Repair => "Repairing", _ => "Uninstalling" };
        progress.Report(new DownloadProgress(plan.Operation is InstallOperation.Uninstall or InstallOperation.Rollback ? DownloadPhase.Installing : DownloadPhase.Downloading,
            $"{verb} {Descriptor.DisplayName} with winget (download, SHA-256 check, install)…", null, null));

        var r = await Winget.RunAsync(args, InstallTimeout, $"{verb} {Descriptor.DisplayName} (winget {Package.PackageId})", ct).ConfigureAwait(false);
        Winget.InvalidateSnapshot();
        Invalidate();
        if (!r.Succeeded)
        {
            // 0x8A15002B on update: nothing newer — not an error for the user, but nothing changed either.
            return new InstallOutcome(false, WingetClient.Explain(r));
        }

        progress.Report(new DownloadProgress(DownloadPhase.Testing, "Checking the result…", null, null));
        var after = await DetectAsync(ct).ConfigureAwait(false);
        return plan.Operation is InstallOperation.Uninstall or InstallOperation.Rollback
            ? new InstallOutcome(!after.Installed, after.Installed ? "winget finished, but the tool is still detected." : $"{Descriptor.DisplayName} was removed.", null, After: after)
            : new InstallOutcome(after.Installed, after.Installed ? $"{Descriptor.DisplayName} {after.Version} is installed." : "winget finished, but the tool was not detected afterwards.", after.Version, After: after);
    }

    /// <summary>Maps an agent detection result to a tool status.</summary>
    protected static ToolStatus FromAgent(string toolId, Domain.Agents.AgentInstallation a, ToolAuthState auth) =>
        new(toolId, !a.Installed ? ToolInstallState.NotInstalled : a.Health is ComponentHealth.Healthy ? ToolInstallState.Installed : ToolInstallState.Broken,
            a.Version, a.ExecutablePath, auth, a.Health, a.Summary);
}
