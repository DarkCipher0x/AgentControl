using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Sdk;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.App.Shell;

/// <summary>One bundled extension and how it relates to what is installed.</summary>
public sealed record GalleryItem(string Folder, ExtensionManifest Manifest, string? InstalledVersion)
{
    public string Id => Manifest.Id;
    public string Name => Manifest.Name;
    public string Description => Manifest.Description;
    public string Version => Manifest.Version;
    public string Kind => string.Join(", ", Manifest.Types.Select(t => t switch
    {
        ExtensionType.StackTemplate => "Stack templates",
        ExtensionType.ModelSource => "Model list",
        _ => t.ToString(),
    }));
    public bool Installed => InstalledVersion is not null;
    public bool UpdateAvailable => InstalledVersion is not null && System.Version.TryParse(InstalledVersion, out var i) && System.Version.TryParse(Version, out var v) && v > i;
    public bool CanInstall => !Installed || UpdateAvailable;
    public string State => UpdateAvailable ? $"Update {InstalledVersion} → {Version}" : Installed ? $"Installed {InstalledVersion}" : "Not installed";
}

/// <summary>
/// The extensions bundled with the app (its install folder's Gallery directory): data-only stack templates and model
/// lists published by DarkCipher Systems. Installing one packs the folder with the same packager as the dcx-ext tool
/// and runs the host's normal review (extraction, manifest, integrity, versions), then the usual review dialog.
/// Nothing is downloaded: everything is already on this PC.
/// </summary>
public sealed class ExtensionGallery(ExtensionHost host, IAppPaths paths, IPackageVerifier verifier, Version appVersion)
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Gallery");

    public IReadOnlyList<GalleryItem> Items()
    {
        if (!Directory.Exists(Root)) return [];
        var installed = host.Extensions.ToDictionary(e => e.Id, e => e.Version, StringComparer.Ordinal);
        var items = new List<GalleryItem>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var file = Path.Combine(dir, SdkInfo.ManifestFileName);
            if (!File.Exists(file) || new FileInfo(file).Length > 64 * 1024) continue;
            var (m, errors) = ManifestValidator.Parse(File.ReadAllText(file));
            if (m is null || errors.Count > 0 || !m.Id.StartsWith(ExtensionHost.GalleryIdPrefix, StringComparison.Ordinal)) continue;
            items.Add(new GalleryItem(dir, m, installed.GetValueOrDefault(m.Id)));
        }
        return items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Packs the bundled folder and stages it for review. The caller shows the review and installs or discards it.</summary>
    public (InstallReview? Review, IReadOnlyList<string> Errors) Prepare(GalleryItem item)
    {
        Directory.CreateDirectory(paths.StagingDirectory);
        var package = Path.Combine(paths.StagingDirectory, $"gallery-{item.Id}-{Guid.NewGuid():N}{SdkInfo.PackageExtension}");
        try
        {
            var packed = ExtensionPackager.Pack(item.Folder, package, appVersion, verifier, bundled: true);
            if (!packed.Success) return (null, packed.Errors);
            return (host.ReviewBundled(package), []);
        }
        finally
        {
            try { if (File.Exists(package)) File.Delete(package); }
            catch (IOException) { /* staging is cleaned on the next start */ }
        }
    }
}
