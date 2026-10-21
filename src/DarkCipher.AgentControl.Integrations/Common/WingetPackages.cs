using System.Text.Json;

namespace DarkCipher.AgentControl.Integrations.Common;

/// <summary>
/// Finds executables inside winget's per-user package folders (%LOCALAPPDATA%\Microsoft\WinGet\Packages\&lt;Id&gt;_&lt;source&gt;).
/// winget does not always create a command link in WinGet\Links (for example when symbolic links are not allowed); it
/// then adds the package folder to the user PATH, which a running app does not see, and the executable keeps its own
/// name (Codex ships "codex-x86_64-pc-windows-msvc.exe", named in codex-package.json). Detection only — launching still
/// goes through the process executor, which re-validates the path.
/// </summary>
public static class WingetPackages
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");

    /// <summary>The package folders for an exact winget id (normally one).</summary>
    public static IEnumerable<string> Folders(string packageId)
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var d in Directory.EnumerateDirectories(Root, packageId + "_*"))
            if (Path.GetFileName(d).StartsWith(packageId + "_", StringComparison.OrdinalIgnoreCase)) yield return d;
    }

    /// <summary>Candidate executables in the package folders: the manifest's entry point (if the package has a
    /// "*-package.json" layout file) and then the given names. Only files directly inside the package folder qualify.</summary>
    public static IEnumerable<string> Candidates(string packageId, params string[] fileNames)
    {
        foreach (var dir in Folders(packageId))
        {
            foreach (var layout in Directory.EnumerateFiles(dir, "*-package.json"))
            {
                var entry = EntryPoint(layout);
                if (entry is not null) yield return Path.Combine(dir, entry);
            }
            foreach (var name in fileNames) yield return Path.Combine(dir, name);
        }
    }

    internal static string? EntryPoint(string layoutFile)
    {
        try
        {
            if (new FileInfo(layoutFile).Length > 64 * 1024) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(layoutFile));
            if (!doc.RootElement.TryGetProperty("entrypoint", out var e) || e.ValueKind != JsonValueKind.String) return null;
            var name = e.GetString();
            // A bare .exe file name only: no separators, no traversal.
            return name is not null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.IndexOfAny(['\\', '/', ':']) < 0 && !name.Contains("..", StringComparison.Ordinal)
                ? name : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
