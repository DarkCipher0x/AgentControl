using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Security.Paths;

/// <summary>
/// Central protected-path enforcement. Every rule and every candidate path is canonicalised (reparse points, 8.3 names,
/// prefixes, case, "..") at evaluation time, so a junction created after a rule was added cannot bypass it.
/// Rules from all matching entries are combined; the most restrictive outcome wins.
/// </summary>
public sealed class ProtectedPathService : IProtectedPathService
{
    private const string StoreName = "protected-paths";
    private readonly object _gate = new();
    private readonly List<ProtectedPathEntry> _builtIn;
    private readonly List<ProtectedPathEntry> _user;
    private readonly IDocumentStore? _store;
    private readonly IAuditLogger? _audit;

    public ProtectedPathService(IAppPaths paths, IDocumentStore? store, IAuditLogger? audit)
        : this(DefaultBuiltIns(paths), store, audit) { }

    /// <summary>Test/advanced constructor with explicit built-in entries.</summary>
    public ProtectedPathService(IEnumerable<ProtectedPathEntry> builtIns, IDocumentStore? store, IAuditLogger? audit)
    {
        _builtIn = builtIns.ToList();
        _store = store;
        _audit = audit;
        _user = (store?.Load(StoreName, () => new UserRules { Entries = [.. DefaultUserRules()] }).Entries ?? [.. DefaultUserRules()])
            .Select(e => e with { BuiltIn = false }).ToList();
    }

    public IReadOnlyList<ProtectedPathEntry> Entries
    {
        get { lock (_gate) return [.. _builtIn, .. _user]; }
    }

    public string Canonicalize(string path) => PathCanonicalizer.Canonicalize(path);

    public static IEnumerable<ProtectedPathEntry> DefaultBuiltIns(IAppPaths paths)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        // App data: the data root itself and every sensitive subfolder are closed to agents and extensions. The
        // disposable test-projects folder and the per-stack agent-profiles folder (e.g. CODEX_HOME) are deliberately
        // NOT covered: agents must be able to work in the sample project and read their isolated profile.
        yield return new(paths.DataRoot, ProtectedPathRule.BlockAllAccess, "DarkCipher AgentControl data folder", true, AppliesToApplication: false, ExactPathOnly: true);
        foreach (var (dir, label) in new[] { (paths.ConfigDirectory, "config"), (paths.AuditDirectory, "audit log"), (paths.LogDirectory, "logs"),
                                             (paths.BackupDirectory, "backups"), (paths.ExtensionsDirectory, "extensions"), (paths.StagingDirectory, "staging") })
            yield return new(dir, ProtectedPathRule.BlockAllAccess, $"DarkCipher AgentControl {label}", true, AppliesToApplication: false);
        if (windows.Length > 0) yield return new(windows, ProtectedPathRule.ReadOnly | ProtectedPathRule.NoAddOnAccess, "Windows system folder", true);
        if (pf.Length > 0) yield return new(pf, ProtectedPathRule.ReadOnly | ProtectedPathRule.NoAddOnAccess, "Program Files", true);
        if (pf86.Length > 0) yield return new(pf86, ProtectedPathRule.ReadOnly | ProtectedPathRule.NoAddOnAccess, "Program Files (x86)", true);
        if (profile.Length > 0)
        {
            foreach (var secretDir in new[] { ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", @"AppData\Roaming\Microsoft\Credentials", @"AppData\Local\Microsoft\Credentials", @"AppData\Roaming\Microsoft\Protect" })
                yield return new(Path.Combine(profile, secretDir), ProtectedPathRule.BlockAllAccess, "Credential store", true, AppliesToApplication: false);
        }
    }

    /// <summary>User rules seeded on first run. The user may edit or remove them.</summary>
    public static IEnumerable<ProtectedPathEntry> DefaultUserRules()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length == 0) yield break;
        // Development-time safeguard (docs/SECURITY.md): the owner's active work tree. Agents get Read-Only unless changed.
        yield return new(Path.Combine(profile, "Projects"), ProtectedPathRule.ReadOnly | ProtectedPathRule.AskBeforeDeletion | ProtectedPathRule.NoAddOnAccess,
            "Active work tree (default safeguard)", false);
    }

    public PathDecision Evaluate(string path, PathOperation operation, PathActor actor = PathActor.Application)
    {
        string canonical;
        try { canonical = PathCanonicalizer.Canonicalize(path); }
        catch (UnsafePathException ex)
        {
            return Record(new PathDecision(PathDecisionKind.Deny, path, "Path refused: " + ex.Message), operation, actor);
        }

        var matched = new List<(ProtectedPathEntry Entry, string Root)>();
        foreach (var e in Entries)
        {
            if (actor == PathActor.Application && !e.AppliesToApplication) continue;
            string root;
            try { root = PathCanonicalizer.Canonicalize(e.Path); }
            catch (UnsafePathException) { continue; }
            var hit = e.ExactPathOnly ? string.Equals(canonical, root, StringComparison.OrdinalIgnoreCase) : PathCanonicalizer.IsSameOrUnder(canonical, root);
            if (hit) matched.Add((e, root));
        }

        var rules = matched.Aggregate(ProtectedPathRule.None, (acc, m) => acc | m.Entry.Rules);
        var top = matched.OrderByDescending(m => m.Root.Length).Select(m => m.Entry).FirstOrDefault();

        var decision = Decide(canonical, operation, actor, rules, top);
        return decision.Kind == PathDecisionKind.Allow ? decision : Record(decision, operation, actor);
    }

    private static PathDecision Decide(string canonical, PathOperation op, PathActor actor, ProtectedPathRule rules, ProtectedPathEntry? top)
    {
        PathDecision Deny(string why) => new(PathDecisionKind.Deny, canonical, why, top);
        PathDecision Ask(string why) => new(PathDecisionKind.RequiresApproval, canonical, why, top);

        if (rules.HasFlag(ProtectedPathRule.BlockAllAccess))
            return Deny($"'{top?.Label}' is protected: all access is blocked.");
        if (op == PathOperation.ExtensionAccess && (rules.HasFlag(ProtectedPathRule.NoAddOnAccess) || actor == PathActor.Extension && rules != ProtectedPathRule.None))
            return Deny($"Extensions may not access '{top?.Label}'.");
        if (op == PathOperation.Execute && rules.HasFlag(ProtectedPathRule.NoProcessExecution))
            return Deny($"Running programs from '{top?.Label}' is blocked.");
        if (op == PathOperation.UseAsWorkingDirectory && rules.HasFlag(ProtectedPathRule.NoProcessExecution))
            return Deny($"Starting processes in '{top?.Label}' is blocked.");

        var writes = op is PathOperation.Write or PathOperation.CreateDirectory;
        if (rules.HasFlag(ProtectedPathRule.ReadOnly))
        {
            if (writes || op == PathOperation.Delete) return Deny($"'{top?.Label}' is read-only.");
            if (op == PathOperation.UseAsWorkingDirectory)
                return Ask($"'{top?.Label}' is read-only. An agent may only be started here with explicit approval and is forced to the Read-Only permission profile.");
        }
        if (writes && rules.HasFlag(ProtectedPathRule.AskBeforeWrites)) return Ask($"'{top?.Label}' requires approval before writes.");
        if (op == PathOperation.Delete && rules.HasFlag(ProtectedPathRule.AskBeforeDeletion)) return Ask($"'{top?.Label}' requires approval before deletion.");

        if (op == PathOperation.UseAsWorkingDirectory)
        {
            if (PathCanonicalizer.IsDriveRoot(canonical)) return Ask("This is the root of a whole drive. Choose a project folder, or approve explicitly.");
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length > 0 && string.Equals(canonical, SafeCanon(profile), StringComparison.OrdinalIgnoreCase))
                return Ask("This is your whole user profile. Choose a project folder, or approve explicitly.");
            if (PathCanonicalizer.IsNetworkPath(canonical)) return Ask("This folder is on a network share. Approve explicitly to use it.");
            if (PathCanonicalizer.IsRemovablePath(canonical)) return Ask("This folder is on removable media. Approve explicitly to use it.");
        }
        if (op == PathOperation.Execute && PathCanonicalizer.IsNetworkPath(canonical))
            return Deny("Programs on network shares are not started (executable substitution risk).");

        return new PathDecision(PathDecisionKind.Allow, canonical, "Allowed", top);
    }

    private static string SafeCanon(string p)
    {
        try { return PathCanonicalizer.Canonicalize(p); } catch (UnsafePathException) { return p; }
    }

    private PathDecision Record(PathDecision d, PathOperation op, PathActor actor)
    {
        _audit?.Record(new AuditEvent(
            d.Kind == PathDecisionKind.Deny ? "ProtectedPathViolation" : "ProtectedPathApprovalRequired",
            "ProtectedPathService",
            d.Kind == PathDecisionKind.Deny ? AuditResult.Blocked : AuditResult.Denied,
            d.CanonicalPath,
            d.Kind == PathDecisionKind.Deny ? SecurityClassification.High : SecurityClassification.Medium,
            Detail: $"{actor} {op}: {d.Reason}"));
        return d;
    }

    public void AddUserRule(string path, ProtectedPathRule rules, string label)
    {
        if (rules == ProtectedPathRule.None) throw new ArgumentException("Choose at least one rule.");
        var canonical = PathCanonicalizer.Canonicalize(path);
        lock (_gate)
        {
            _user.RemoveAll(e => string.Equals(SafeCanon(e.Path), canonical, StringComparison.OrdinalIgnoreCase));
            _user.Add(new ProtectedPathEntry(canonical, rules, string.IsNullOrWhiteSpace(label) ? canonical : label.Trim(), false));
            Persist();
        }
        _audit?.Record(new AuditEvent("ProtectedPathAdded", "ProtectedPathService", AuditResult.Success, canonical, SecurityClassification.Medium, Detail: rules.ToString()));
    }

    public bool RemoveUserRule(string path)
    {
        string canonical;
        try { canonical = PathCanonicalizer.Canonicalize(path); } catch (UnsafePathException) { canonical = path; }
        int removed;
        lock (_gate)
        {
            removed = _user.RemoveAll(e => string.Equals(SafeCanon(e.Path), canonical, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) Persist();
        }
        if (removed > 0)
            _audit?.Record(new AuditEvent("ProtectedPathRemoved", "ProtectedPathService", AuditResult.Success, canonical, SecurityClassification.High));
        return removed > 0;
    }

    private void Persist() => _store?.Save(StoreName, new UserRules { Entries = [.. _user] });

    public sealed class UserRules
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ProtectedPathEntry> Entries { get; set; } = [];
    }
}
