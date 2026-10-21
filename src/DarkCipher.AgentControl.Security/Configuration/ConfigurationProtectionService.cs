using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Security.Configuration;

/// <summary>
/// Changes to files the app does not own (agent configs and similar) always go: Preview (diff) → Apply (protected-path
/// check, concurrency check, backup, atomic replace, audit) → optional Rollback. Writes are atomic: content is written
/// to a temporary file in the same directory, flushed, then swapped in with File.Replace/Move.
/// </summary>
public sealed class ConfigurationProtectionService : IConfigurationProtection
{
    private const long MaxConfigBytes = 2 * 1024 * 1024;
    private const int KeepBackups = 20;
    private readonly IProtectedPathService _paths;
    private readonly IAuditLogger _audit;
    private readonly string _backupRoot;

    public ConfigurationProtectionService(IProtectedPathService paths, IAuditLogger audit, IAppPaths appPaths)
        : this(paths, audit, appPaths.BackupDirectory) { }

    public ConfigurationProtectionService(IProtectedPathService paths, IAuditLogger audit, string backupRoot)
    {
        _paths = paths;
        _audit = audit;
        _backupRoot = backupRoot;
        Directory.CreateDirectory(_backupRoot);
    }

    public ConfigChangePreview Preview(string targetPath, string newContent)
    {
        var canonical = _paths.Canonicalize(targetPath);
        string? current = null;
        if (File.Exists(canonical))
        {
            if (new FileInfo(canonical).Length > MaxConfigBytes) throw new InvalidDataException("The existing file is too large to manage safely.");
            current = File.ReadAllText(canonical, Encoding.UTF8);
        }
        return new ConfigChangePreview(canonical, current is not null, current, newContent, LineDiff.Compute(current ?? "", newContent));
    }

    public OperationResult<ConfigBackup?> Apply(ConfigChangePreview preview, CorrelationId correlation)
    {
        if (Encoding.UTF8.GetByteCount(preview.NewContent) > MaxConfigBytes) return OperationResult<ConfigBackup?>.Fail("The new content is too large.");
        var decision = _paths.Evaluate(preview.TargetPath, PathOperation.Write);
        if (decision.Kind != PathDecisionKind.Allow)
            return OperationResult<ConfigBackup?>.Fail("Not written: " + decision.Reason, "protected-path");

        var target = decision.CanonicalPath;
        var exists = File.Exists(target);
        var current = exists ? File.ReadAllText(target, Encoding.UTF8) : null;
        if (exists != preview.TargetExists || !string.Equals(current, preview.CurrentContent, StringComparison.Ordinal))
            return OperationResult<ConfigBackup?>.Fail("The file changed after the preview was made. Preview again before applying.", "stale-preview");

        ConfigBackup? backup = null;
        try
        {
            if (exists) backup = CreateBackup(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            AtomicWrite(target, preview.NewContent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _audit.Record(new AuditEvent("ConfigChangeFailed", "ConfigurationProtectionService", AuditResult.Failure, target, SecurityClassification.Medium, correlation, ex.Message));
            return OperationResult<ConfigBackup?>.Fail("The file could not be written: " + ex.Message);
        }

        _audit.Record(new AuditEvent("ConfigChanged", "ConfigurationProtectionService", AuditResult.Success, target, SecurityClassification.Medium, correlation,
            backup is null ? "created" : $"backup {Path.GetFileName(backup.BackupPath)}"));
        return OperationResult<ConfigBackup?>.Ok(backup, backup is null ? "Created" : "Applied (previous version backed up)");
    }

    public OperationResult Rollback(ConfigBackup backup, CorrelationId correlation)
    {
        var decision = _paths.Evaluate(backup.TargetPath, PathOperation.Write);
        if (decision.Kind != PathDecisionKind.Allow) return OperationResult.Fail("Not restored: " + decision.Reason);
        var backupCanonical = _paths.Canonicalize(backup.BackupPath);
        if (!Paths.PathCanonicalizer.IsSameOrUnder(backupCanonical, _paths.Canonicalize(_backupRoot)) || !File.Exists(backupCanonical))
            return OperationResult.Fail("That backup is not one of this app's backups.");
        try
        {
            if (File.Exists(decision.CanonicalPath)) CreateBackup(decision.CanonicalPath);
            AtomicWrite(decision.CanonicalPath, File.ReadAllText(backupCanonical, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail("Restore failed: " + ex.Message);
        }
        _audit.Record(new AuditEvent("ConfigRolledBack", "ConfigurationProtectionService", AuditResult.Success, decision.CanonicalPath, SecurityClassification.Medium, correlation));
        return OperationResult.Ok("Restored");
    }

    public OperationResult RemoveCreated(string targetPath, string expectedContent, CorrelationId correlation)
    {
        var decision = _paths.Evaluate(targetPath, PathOperation.Delete);
        if (decision.Kind != PathDecisionKind.Allow) return OperationResult.Fail("Not removed: " + decision.Reason);
        var target = decision.CanonicalPath;
        if (!File.Exists(target)) return OperationResult.Ok("Already gone.");
        if (new FileInfo(target).Length > MaxConfigBytes) return OperationResult.Fail("The file is too large to manage safely.");
        var current = File.ReadAllText(target, Encoding.UTF8);
        // Only remove what this app wrote: if anyone changed the file since, leave it (and say so).
        if (!string.Equals(current, expectedContent, StringComparison.Ordinal))
            return OperationResult.Fail("The file changed after this app created it, so it was kept. Review it by hand.");
        try
        {
            CreateBackup(target);
            File.Delete(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OperationResult.Fail("Could not remove the file: " + ex.Message);
        }
        _audit.Record(new AuditEvent("ConfigRemoved", "ConfigurationProtectionService", AuditResult.Success, target, SecurityClassification.Medium, correlation, "rolled back a created file"));
        return OperationResult.Ok("Removed the file this change had created (a backup was kept).");
    }

    public string? Read(string targetPath)
    {
        var decision = _paths.Evaluate(targetPath, PathOperation.Read);
        if (decision.Kind == PathDecisionKind.Deny) throw new UnauthorizedAccessException(decision.Reason);
        var target = decision.CanonicalPath;
        if (!File.Exists(target)) return null;
        if (new FileInfo(target).Length > MaxConfigBytes) throw new InvalidDataException("The file is too large to manage safely.");
        return File.ReadAllText(target, Encoding.UTF8);
    }

    public IReadOnlyList<ConfigBackup> Backups(string targetPath)
    {
        var dir = BackupDirFor(_paths.Canonicalize(targetPath));
        if (!Directory.Exists(dir)) return [];
        var target = _paths.Canonicalize(targetPath);
        return new DirectoryInfo(dir).GetFiles("*.bak").OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => new ConfigBackup(target, f.FullName, new DateTimeOffset(f.CreationTimeUtc, TimeSpan.Zero), f.Length)).ToList();
    }

    private ConfigBackup CreateBackup(string target)
    {
        var dir = BackupDirFor(target);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "target.json"), JsonSerializer.Serialize(new { target }));
        var name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".bak";
        var path = Path.Combine(dir, name);
        File.Copy(target, path, overwrite: false);
        foreach (var old in new DirectoryInfo(dir).GetFiles("*.bak").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepBackups))
            old.Delete();
        return new ConfigBackup(target, path, DateTimeOffset.UtcNow, new FileInfo(path).Length);
    }

    private string BackupDirFor(string canonicalTarget)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalTarget.ToUpperInvariant())))[..16];
        return Path.Combine(_backupRoot, hash);
    }

    /// <summary>Write-to-temp, flush, then atomic swap. The temp file lives beside the target so the rename never
    /// crosses volumes.</summary>
    public static void AtomicWrite(string target, string content)
    {
        var dir = Path.GetDirectoryName(target)!;
        var temp = Path.Combine(dir, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                fs.Write(bytes);
                fs.Flush(true);
            }
            if (File.Exists(target)) File.Replace(temp, target, null, ignoreMetadataErrors: true);
            else File.Move(temp, target);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

/// <summary>Small LCS line diff for previews ("+ added", "- removed", "  same"). Bounded for very large inputs.</summary>
public static class LineDiff
{
    public static IReadOnlyList<string> Compute(string before, string after, int maxLines = 2000)
    {
        var a = before.Replace("\r\n", "\n").Split('\n');
        var b = after.Replace("\r\n", "\n").Split('\n');
        if (a.Length > maxLines || b.Length > maxLines)
            return [$"(file too large for a line diff: {a.Length} → {b.Length} lines)"];

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var result = new List<string>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { result.Add("  " + a[x]); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) result.Add("- " + a[x++]);
            else result.Add("+ " + b[y++]);
        }
        while (x < a.Length) result.Add("- " + a[x++]);
        while (y < b.Length) result.Add("+ " + b[y++]);
        return result;
    }
}
