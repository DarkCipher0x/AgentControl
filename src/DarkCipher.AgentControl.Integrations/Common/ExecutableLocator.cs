namespace DarkCipher.AgentControl.Integrations.Common;

/// <summary>Locates an executable for DETECTION. Launching still goes through the process executor, which re-validates
/// the path. Relative PATH entries (including ".") are ignored so the current directory can never supply a binary.</summary>
public static class ExecutableLocator
{
    public static string? Find(string fileName, IEnumerable<string> knownLocations)
    {
        if (fileName.IndexOfAny(['\\', '/', ':']) >= 0 || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Pass a bare .exe file name.");
        foreach (var p in knownLocations)
            if (File.Exists(p)) return Path.GetFullPath(p);
        return FindOnPath(fileName);
    }

    public static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var d = dir.Trim('"');
            if (!Path.IsPathFullyQualified(d)) continue;
            try
            {
                var candidate = Path.Combine(d, fileName);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Finds a script shim (e.g. npm's "claude.cmd") purely to explain why it is not launched.</summary>
    public static string? FindShimOnPath(string baseName)
    {
        foreach (var ext in new[] { ".cmd", ".bat", ".ps1" })
        {
            var found = FindOnPathAnyExtension(baseName + ext);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? FindOnPathAnyExtension(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(dir)) continue;
            var c = Path.Combine(dir, fileName);
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
