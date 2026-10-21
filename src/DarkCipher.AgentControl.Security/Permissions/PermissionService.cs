using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Security.Permissions;

/// <summary>Resolves agent permission profiles (built-in and custom) and computes the effective profile for a launch:
/// a project folder whose protected-path rule is Read-Only forces the Read-Only profile, whatever was requested.</summary>
public sealed class PermissionService : IPermissionService
{
    private const string StoreName = "permission-profiles";
    private readonly IDocumentStore? _store;
    private readonly IAuditLogger _audit;
    private readonly List<PermissionProfile> _custom;

    public PermissionService(IDocumentStore? store, IAuditLogger audit)
    {
        _store = store;
        _audit = audit;
        _custom = store?.Load(StoreName, () => new CustomProfiles()).Profiles.Select(p => p with { BuiltIn = false }).ToList() ?? [];
    }

    public IReadOnlyList<PermissionProfile> Profiles => [.. BuiltInPermissionProfiles.All(), .. _custom];

    public PermissionProfile Resolve(string? id) =>
        Profiles.FirstOrDefault(p => p.Id == id) ?? BuiltInPermissionProfiles.Get(BuiltInPermissionProfiles.DefaultId);

    public (PermissionProfile Profile, string? Note) Effective(PermissionProfile requested, PathDecision projectDecision)
    {
        if (projectDecision.MatchedRule?.Rules.HasFlag(ProtectedPathRule.ReadOnly) == true && requested.Id != BuiltInPermissionProfiles.ReadOnlyId)
            return (BuiltInPermissionProfiles.Get(BuiltInPermissionProfiles.ReadOnlyId),
                $"'{projectDecision.MatchedRule.Label}' is read-only, so the session uses the Read-Only profile instead of '{requested.Name}'.");
        return (requested, null);
    }

    public OperationResult SaveCustom(PermissionProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || !profile.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return OperationResult.Fail("Profile id may contain letters, digits and '-'.");
        if (BuiltInPermissionProfiles.All().Any(p => p.Id == profile.Id)) return OperationResult.Fail("Built-in profiles cannot be replaced.");
        _custom.RemoveAll(p => p.Id == profile.Id);
        _custom.Add(profile with { BuiltIn = false });
        _store?.Save(StoreName, new CustomProfiles { Profiles = [.. _custom] });
        _audit.Record(new AuditEvent("PermissionProfileChanged", "PermissionService", AuditResult.Success, profile.Id, SecurityClassification.Medium,
            Detail: string.Join(", ", profile.Grants.Where(g => g.Value != PermissionGrant.Deny).Select(g => $"{g.Key}={g.Value}"))));
        return OperationResult.Ok("Saved");
    }

    public sealed class CustomProfiles
    {
        public int SchemaVersion { get; set; } = 1;
        public List<PermissionProfile> Profiles { get; set; } = [];
    }
}
