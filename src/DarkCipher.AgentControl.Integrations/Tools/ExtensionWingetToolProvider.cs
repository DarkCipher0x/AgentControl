using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.Integrations.Tools;

/// <summary>
/// A catalogue entry contributed by an extension (SDK 1.1). The extension supplies data only; this host adapter installs
/// it with winget (exact id, community source, plan + approval like every other tool). It is always labelled Unverified
/// and is never accepted by templates until validated. Detection uses winget's own installed-package list.
/// </summary>
public sealed class ExtensionWingetToolProvider : WingetToolProvider
{
    private readonly AgentToolDescriptor _descriptor;

    public ExtensionWingetToolProvider(WingetClient winget, string extensionId, string id, string displayName, string publisher, string description,
        string packageId, string homepage, string licence, IReadOnlyList<string> protocols) : base(winget)
    {
        if (!WingetClient.IsValidPackageId(packageId)) throw new ArgumentException($"'{packageId}' is not a valid winget package id.");
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var home) || home.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The homepage must be an https URL.");
        _descriptor = new AgentToolDescriptor
        {
            Id = "ext-" + new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(40).ToArray()).ToLowerInvariant(),
            DisplayName = Trim(displayName, 60),
            Publisher = Trim(publisher, 60),
            Description = Trim(description, 300) + $" (catalogue entry from extension {extensionId})",
            Kind = ToolKind.AgentTool,
            Trust = ToolTrust.Unverified,
            Source = new PackageSource("winget", packageId, "winget", Trim(publisher, 60), home.ToString(), Trim(licence, 80)),
            InstallMethod = "winget (Windows Package Manager), per-user — entry contributed by an extension",
            Authentication = "Unknown (not described by the extension)",
            SupportedProtocols = protocols.Take(8).Select(p => Trim(p, 40)).ToList(),
            KnownLimitations = ["Unverified: contributed by an extension and not reviewed by DarkCipher Systems.", "Sessions with this tool are not supported until a provider exists."],
            Security = SecurityClassification.High,
            EstimatedDiskBytes = 500L * 1000 * 1000,
            AffectedLocations = [@"%LOCALAPPDATA%\Microsoft\WinGet\Packages\" + packageId + "_*"],
            EnvironmentChanges = ["Possibly user-level PATH (winget portable packages). Shown again in the plan."],
        };
    }

    public override AgentToolDescriptor Descriptor => _descriptor;

    public override async Task<ToolStatus> DetectAsync(CancellationToken ct)
    {
        var version = await Winget.InstalledVersionAsync(Package.PackageId, ct).ConfigureAwait(false);
        return version is null
            ? new ToolStatus(_descriptor.Id, ToolInstallState.NotInstalled, null, null, ToolAuthState.Unknown, ComponentHealth.Unknown, "Not installed (according to winget).")
            : new ToolStatus(_descriptor.Id, ToolInstallState.Installed, version, null, ToolAuthState.Unknown, ComponentHealth.Healthy, $"Installed {version} (according to winget).");
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];
}
