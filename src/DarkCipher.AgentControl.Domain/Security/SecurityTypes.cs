using DarkCipher.AgentControl.Domain.Common;

namespace DarkCipher.AgentControl.Domain.Security;

/// <summary>Rules applied to a protected path. Flags combine; BlockAllAccess dominates.</summary>
[Flags]
public enum ProtectedPathRule
{
    None = 0,
    BlockAllAccess = 1,
    ReadOnly = 2,
    AskBeforeWrites = 4,
    AskBeforeDeletion = 8,
    NoProcessExecution = 16,
    NoAddOnAccess = 32,
}

public enum PathOperation { Read, Write, Delete, CreateDirectory, Execute, UseAsWorkingDirectory, ExtensionAccess }

public enum PathDecisionKind { Allow, Deny, RequiresApproval }

public sealed record PathDecision(PathDecisionKind Kind, string CanonicalPath, string Reason, ProtectedPathEntry? MatchedRule = null)
{
    public bool Allowed => Kind == PathDecisionKind.Allow;
}

/// <summary>Who wants to touch the path. User-defined rules bind everyone, including this application.</summary>
public enum PathActor { Application, Agent, Extension }

/// <param name="AppliesToApplication">False only for the built-in rule that shields the app's own data directory from
/// agents and extensions (the app must still write its own data there).</param>
/// <param name="ExactPathOnly">Matches only the folder itself, not its descendants (used for the app data root).</param>
public sealed record ProtectedPathEntry(string Path, ProtectedPathRule Rules, string Label, bool BuiltIn, bool AppliesToApplication = true, bool ExactPathOnly = false);

/// <summary>Port: canonical-path protection. Never string-prefix based.</summary>
public interface IProtectedPathService
{
    IReadOnlyList<ProtectedPathEntry> Entries { get; }
    PathDecision Evaluate(string path, PathOperation operation, PathActor actor = PathActor.Application);
    void AddUserRule(string path, ProtectedPathRule rules, string label);
    bool RemoveUserRule(string path);
    /// <summary>Canonical form (full, long-name, reparse points resolved). Throws for unusable input.</summary>
    string Canonicalize(string path);
}

public enum AuditResult { Success, Failure, Blocked, Denied, Approved, Cancelled }

public enum SecurityClassification { Informational, Low, Medium, High, Critical }

public sealed record AuditEvent(
    string Action,
    string Component,
    AuditResult Result,
    string? AffectedResource,
    SecurityClassification Classification,
    CorrelationId? Correlation = null,
    string? Detail = null)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Port: tamper-evident security audit log. Never receives secret values.</summary>
public interface IAuditLogger
{
    void Record(AuditEvent auditEvent);
    IReadOnlyList<AuditEvent> Recent(int max = 500);
    /// <summary>Verifies the hash chain; returns the number of the first broken line or null if intact.</summary>
    AuditVerification Verify();
}

public sealed record AuditVerification(bool Intact, int Lines, int? FirstBrokenLine, string Message);

/// <summary>Opaque handle to a stored secret. Carries no secret material.</summary>
public sealed record CredentialReference(string Id, string DisplayName, string Kind, DateTimeOffset CreatedAt);

/// <summary>Port: secret storage (Windows Credential Manager). Values never leave the security layer except into a
/// child process environment or an outgoing request header assembled inside the security layer.</summary>
public interface ICredentialStore
{
    IReadOnlyList<CredentialReference> List();
    CredentialReference Save(string displayName, string kind, string secret);
    bool Remove(string id);
    bool Exists(string id);
}

/// <summary>Port: removes secrets from any text before it is logged, displayed or exported.</summary>
public interface IRedactor
{
    string Redact(string? text);
    void RegisterSecret(string secret);
}

/// <summary>Port: approval prompts. The UI implements it; headless/test hosts deny by default.</summary>
public interface IApprovalService
{
    Task<bool> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

public sealed record ApprovalRequest(string Title, string Summary, IReadOnlyList<string> Details, SecurityClassification Risk, string ApproveLabel = "Approve");

public sealed class DenyAllApprovals : IApprovalService
{
    public Task<bool> RequestAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult(false);
}
