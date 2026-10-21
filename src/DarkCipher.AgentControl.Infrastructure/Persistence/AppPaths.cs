using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Security.Paths;

namespace DarkCipher.AgentControl.Infrastructure.Persistence;

/// <summary>
/// Well-known application directories under %LOCALAPPDATA%\DarkCipher\AgentControl (never roaming, never synced).
/// DCAC_DATA_DIR overrides the root for tests and the self-test. The root is refused if it resolves into a protected
/// development tree, a network share or removable media.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public const string DataDirVariable = "DCAC_DATA_DIR";

    public AppPaths(string? overrideRoot = null)
    {
        var root = overrideRoot
            ?? Environment.GetEnvironmentVariable(DataDirVariable)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DarkCipher", "AgentControl");
        var canonical = PathCanonicalizer.Canonicalize(root);

        var projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects");
        if (PathCanonicalizer.IsSameOrUnder(canonical, PathCanonicalizer.Canonicalize(projects)))
            throw new InvalidOperationException("The data folder may not be inside the protected Projects tree.");
        if (PathCanonicalizer.IsNetworkPath(canonical)) throw new InvalidOperationException("The data folder may not be on a network share.");

        DataRoot = canonical;
        foreach (var d in All) Directory.CreateDirectory(d);
    }

    public string DataRoot { get; }
    public string ConfigDirectory => Path.Combine(DataRoot, "config");
    public string LogDirectory => Path.Combine(DataRoot, "logs");
    public string AuditDirectory => Path.Combine(DataRoot, "audit");
    public string BackupDirectory => Path.Combine(DataRoot, "backups");
    public string ExtensionsDirectory => Path.Combine(DataRoot, "extensions");
    public string StagingDirectory => Path.Combine(DataRoot, "staging");
    public string AgentProfilesDirectory => Path.Combine(DataRoot, "agent-profiles");
    public string TestProjectsDirectory => Path.Combine(DataRoot, "test-projects");

    private IEnumerable<string> All => [DataRoot, ConfigDirectory, LogDirectory, AuditDirectory, BackupDirectory, ExtensionsDirectory, StagingDirectory, AgentProfilesDirectory, TestProjectsDirectory];
}
