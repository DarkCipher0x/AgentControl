using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Network;

namespace DarkCipher.AgentControl.Security.Policy;

/// <summary>Holds, persists and audits the application security policy. Changes that weaken security are audited as
/// High; command-line Safe Mode cannot be switched off from inside a running session.</summary>
public sealed class SecurityPolicyService : ISecurityPolicyService
{
    private const string StoreName = "security-policy";
    private readonly object _gate = new();
    private readonly IDocumentStore? _store;
    private readonly IAuditLogger _audit;
    private readonly bool _safeModeFromCommandLine;
    private SecurityPolicy _current;

    public SecurityPolicyService(IDocumentStore? store, IAuditLogger audit, bool safeModeFromCommandLine = false)
    {
        _store = store;
        _audit = audit;
        _safeModeFromCommandLine = safeModeFromCommandLine;
        var loaded = store?.Load(StoreName, () => new PolicyDocument()).Policy ?? new SecurityPolicy();
        _current = Sanitize(loaded with { SafeMode = loaded.SafeMode || safeModeFromCommandLine });
    }

    public SecurityPolicy Current { get { lock (_gate) return _current; } }

    public event EventHandler<SecurityPolicy>? Changed;

    public OperationResult Update(Func<SecurityPolicy, SecurityPolicy> change, string reason)
    {
        SecurityPolicy before, after;
        lock (_gate)
        {
            before = _current;
            after = Sanitize(change(before));
            if (_safeModeFromCommandLine) after = after with { SafeMode = true };
            if (after == before) return OperationResult.Ok("No change");
            _current = after;
            _store?.Save(StoreName, new PolicyDocument { Policy = after with { SafeMode = after.SafeMode && !_safeModeFromCommandLine } });
        }

        var weakened = Weakens(before, after);
        _audit.Record(new AuditEvent("SecurityPolicyChanged", "SecurityPolicyService", AuditResult.Success, null,
            weakened ? SecurityClassification.High : SecurityClassification.Medium, Detail: $"{reason}: {Describe(before, after)}"));
        Changed?.Invoke(this, after);
        return OperationResult.Ok("Saved");
    }

    public bool IsEndpointApproved(Uri uri)
    {
        var key = NetworkClassifier.EndpointKey(uri);
        return Current.ApprovedEndpoints.Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    public OperationResult ApproveEndpoint(Uri uri, string reason)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return OperationResult.Fail("Only http/https endpoints can be approved.");
        var key = NetworkClassifier.EndpointKey(uri);
        var cls = NetworkClassifier.Classify(uri);
        if (uri.Scheme == Uri.UriSchemeHttp && cls != NetworkClass.LocalNetwork && cls != NetworkClass.Loopback)
            return OperationResult.Fail("Plain HTTP can only be approved for local-network addresses. Use HTTPS.");
        var r = Update(p => p with { ApprovedEndpoints = p.ApprovedEndpoints.Append(key).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }, reason);
        _audit.Record(new AuditEvent("RemoteEndpointApproved", "SecurityPolicyService", AuditResult.Approved, key, SecurityClassification.High, Detail: $"{cls}: {reason}"));
        return r;
    }

    public OperationResult RevokeEndpoint(string key) =>
        Update(p => p with { ApprovedEndpoints = p.ApprovedEndpoints.Where(e => !e.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList() }, "Endpoint revoked");

    private static SecurityPolicy Sanitize(SecurityPolicy p) => p with
    {
        ApprovedEndpoints = p.ApprovedEndpoints.Where(e => Uri.TryCreate(e, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList(),
    };

    private static bool Weakens(SecurityPolicy a, SecurityPolicy b) =>
        (!a.DevelopmentMode && b.DevelopmentMode) || (a.DisableThirdPartyExtensions && !b.DisableThirdPartyExtensions)
        || (!a.AllowRemoteRuntimes && b.AllowRemoteRuntimes) || (a.RequireHttpsForRemote && !b.RequireHttpsForRemote)
        || b.Privacy > a.Privacy || (a.SafeMode && !b.SafeMode) || b.ApprovedEndpoints.Count > a.ApprovedEndpoints.Count;

    private static string Describe(SecurityPolicy a, SecurityPolicy b)
    {
        var parts = new List<string>();
        if (a.Privacy != b.Privacy) parts.Add($"privacy {a.Privacy}→{b.Privacy}");
        if (a.DevelopmentMode != b.DevelopmentMode) parts.Add($"development mode {(b.DevelopmentMode ? "ON" : "off")}");
        if (a.DisableThirdPartyExtensions != b.DisableThirdPartyExtensions) parts.Add($"third-party extensions {(b.DisableThirdPartyExtensions ? "disabled" : "ENABLED")}");
        if (a.AllowRemoteRuntimes != b.AllowRemoteRuntimes) parts.Add($"remote runtimes {(b.AllowRemoteRuntimes ? "ALLOWED" : "blocked")}");
        if (a.RequireHttpsForRemote != b.RequireHttpsForRemote) parts.Add($"HTTPS for remote {(b.RequireHttpsForRemote ? "required" : "NOT required")}");
        if (a.SafeMode != b.SafeMode) parts.Add($"safe mode {(b.SafeMode ? "on" : "off")}");
        if (a.ApprovedEndpoints.Count != b.ApprovedEndpoints.Count) parts.Add($"approved endpoints {a.ApprovedEndpoints.Count}→{b.ApprovedEndpoints.Count}");
        return parts.Count == 0 ? "no material change" : string.Join(", ", parts);
    }

    public sealed class PolicyDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public SecurityPolicy Policy { get; set; } = new();
    }
}
