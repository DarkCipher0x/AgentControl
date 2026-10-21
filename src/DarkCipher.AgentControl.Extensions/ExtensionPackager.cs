using System.IO.Compression;
using DarkCipher.AgentControl.Sdk;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Extensions;

/// <summary>
/// Builds a .dcxext package from an extension folder: validates the manifest, copies only what belongs in the package
/// (manifest, contributed files and, for code extensions, assemblies) into a staging folder, writes files.sha256, zips
/// it, then extracts and verifies the finished package exactly as the host will. Used by the dcx-ext tool and by the
/// app's bundled extension gallery, so both produce identical, verified packages.
/// </summary>
public static class ExtensionPackager
{
    public sealed record PackResult(bool Success, IReadOnlyList<string> Errors, ExtensionManifest? Manifest, string? Sha256);

    /// <param name="bundled">Only for the app's own gallery: allows the reserved DarkCipher identity for data-only packages
    /// with the gallery id prefix (the dcx-ext tool never sets it).</param>
    public static PackResult Pack(string folder, string output, Version appVersion, IPackageVerifier verifier, bool bundled = false)
    {
        if (!output.EndsWith(SdkInfo.PackageExtension, StringComparison.OrdinalIgnoreCase)) return Fail($"Output must end with {SdkInfo.PackageExtension}.");
        var manifestPath = Path.Combine(folder, SdkInfo.ManifestFileName);
        if (!File.Exists(manifestPath)) return Fail($"No {SdkInfo.ManifestFileName} in {folder}.");
        var (m, parse) = ManifestValidator.Parse(File.ReadAllText(manifestPath));
        if (m is null || parse.Count > 0)
        {
            var errors = parse.ToList();
            if (m is null) errors.Add("The manifest could not be read.");
            return Fail([.. errors]);
        }
        var invalid = ManifestValidator.Validate(m, appVersion, builtIn: bundled && m.IsDeclarative && m.Id.StartsWith(ExtensionHost.GalleryIdPrefix, StringComparison.Ordinal));
        if (invalid.Count > 0) return new PackResult(false, invalid, m, null);

        var outDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(outDir);
        var staging = Path.Combine(outDir, ".dcx-ext-staging-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(staging);
            var include = new List<string> { SdkInfo.ManifestFileName };
            include.AddRange(m.Contributes.StackTemplates.Concat(m.Contributes.ModelCatalogs));
            if (!m.IsDeclarative)
                include.AddRange(Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).Select(f => Path.GetFileName(f))
                    .Where(n => n != "DarkCipher.AgentControl.Sdk.dll"));
            foreach (var rel in include.Distinct())
            {
                var src = Path.Combine(folder, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(src)) return new PackResult(false, [$"Missing {rel}."], m, null);
                var dst = Path.Combine(staging, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst);
            }
            File.WriteAllText(Path.Combine(staging, SdkInfo.IntegrityFileName), PackageIntegrity.Build(staging, verifier));
            if (File.Exists(output)) File.Delete(output);
            ZipFile.CreateFromDirectory(staging, output, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally { TryDelete(staging); }

        // Re-verify the finished package exactly as the host will.
        var check = Path.Combine(outDir, ".dcx-ext-verify-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var x = verifier.ExtractZipSafely(output, check, new ExtractionLimits(MaxEntries: 500));
            var problems = x.Success ? PackageIntegrity.Verify(check, verifier, allowDlls: !m.IsDeclarative) : [x.Message];
            return problems.Count > 0 ? new PackResult(false, problems, m, null) : new PackResult(true, [], m, verifier.ComputeSha256(output));
        }
        finally { TryDelete(check); }

        static PackResult Fail(params string[] errors) => new(false, errors, null, null);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* staging folders are cleaned on the next run */ }
    }
}
