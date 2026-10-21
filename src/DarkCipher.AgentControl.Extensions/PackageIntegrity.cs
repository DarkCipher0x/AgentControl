using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Sdk;

namespace DarkCipher.AgentControl.Extensions;

/// <summary>The files.sha256 integrity list ("&lt;sha256&gt;  &lt;relative/path&gt;" per line). Every file in a package must be
/// listed with a matching hash and nothing unlisted may be present; the list is re-checked at every start, so files
/// changed after installation are detected (tamper check).</summary>
public static class PackageIntegrity
{
    public static readonly string[] ForbiddenExtensions = [".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".msi", ".msp", ".scr", ".lnk", ".hta", ".cpl", ".sys", ".drv"];

    public static string Build(string root, IPackageVerifier verifier)
    {
        var lines = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(r => r != SdkInfo.IntegrityFileName)
            .OrderBy(r => r, StringComparer.Ordinal)
            .Select(r => $"{verifier.ComputeSha256(Path.Combine(root, r))}  {r}");
        return string.Join('\n', lines) + "\n";
    }

    public static List<string> Verify(string root, IPackageVerifier verifier, bool allowDlls)
    {
        var errors = new List<string>();
        var listPath = Path.Combine(root, SdkInfo.IntegrityFileName);
        if (!File.Exists(listPath)) { errors.Add($"The package has no {SdkInfo.IntegrityFileName} integrity list."); return errors; }

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(listPath).Where(l => l.Length > 0))
        {
            var parts = line.Split("  ", 2);
            if (parts.Length != 2 || parts[0].Length != 64 || !ManifestValidatorPathOk(parts[1])) { errors.Add($"Malformed integrity line: '{line}'."); continue; }
            if (!expected.TryAdd(parts[1], parts[0])) errors.Add($"Duplicate integrity entry for '{parts[1]}'.");
        }

        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(r => r != SdkInfo.IntegrityFileName).ToList();

        foreach (var rel in actual)
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ForbiddenExtensions.Contains(ext)) errors.Add($"'{rel}': executable or script files are not allowed in extensions.");
            if (ext == ".dll" && !allowDlls) errors.Add($"'{rel}': declarative extensions may not contain assemblies.");
            if (!expected.TryGetValue(rel, out var hash)) { errors.Add($"'{rel}' is not listed in the integrity file."); continue; }
            if (!verifier.VerifySha256(Path.Combine(root, rel), hash)) errors.Add($"'{rel}' does not match its recorded hash (modified or corrupted).");
        }
        foreach (var missing in expected.Keys.Except(actual, StringComparer.OrdinalIgnoreCase)) errors.Add($"'{missing}' is listed but missing.");
        return errors;
    }

    private static bool ManifestValidatorPathOk(string p) =>
        !string.IsNullOrWhiteSpace(p) && !Path.IsPathRooted(p) && !p.Contains(':') && !p.Contains('\\') && p.Split('/').All(s => s.Length > 0 && s != "." && s != "..");
}
