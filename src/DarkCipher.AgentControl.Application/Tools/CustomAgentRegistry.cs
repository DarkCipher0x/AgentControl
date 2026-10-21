using System.Text.RegularExpressions;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.Application.Tools;

/// <summary>
/// Custom agent tools registered by the user (advanced). Definitions are validated (id, full .exe path, bounded argument
/// and environment lists, no secrets in values) and start Unverified. They become usable by templates only after a local
/// validation test (the version command through the secure executor) has passed. Nothing is ever installed for them.
/// </summary>
public sealed partial class CustomAgentRegistry
{
    private const string StoreName = "custom-agents";
    private readonly IDocumentStore _store;
    private readonly IAuditLogger _audit;
    private readonly object _gate = new();
    private List<CustomAgentDefinition> _all;

    public CustomAgentRegistry(IDocumentStore store, IAuditLogger audit)
    {
        _store = store;
        _audit = audit;
        _all = store.Load(StoreName, () => new Doc()).Agents.Where(a => Validate(a).Count == 0).ToList();
    }

    public IReadOnlyList<CustomAgentDefinition> All { get { lock (_gate) return [.. _all]; } }

    public event EventHandler? Changed;

    [GeneratedRegex(@"^custom-[a-z0-9][a-z0-9\-]{0,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvName();

    /// <summary>Things that look like credentials are refused in any definition value (use the credential store instead).</summary>
    [GeneratedRegex(@"(sk-[A-Za-z0-9_\-]{16,}|hf_[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|(?i:password|secret|token|apikey|api_key)\s*=)", RegexOptions.CultureInvariant)]
    private static partial Regex SecretLike();

    public static bool LooksLikeSecret(string? s) => s is not null && SecretLike().IsMatch(s);

    public static IReadOnlyList<string> Validate(CustomAgentDefinition d)
    {
        var problems = new List<string>();
        if (!IdPattern().IsMatch(d.Id ?? "")) problems.Add("Id must look like 'custom-my-agent' (lower case, digits, dashes).");
        if (string.IsNullOrWhiteSpace(d.DisplayName) || d.DisplayName.Length > 60) problems.Add("Display name is required (max 60 characters).");
        if (string.IsNullOrWhiteSpace(d.Executable) || !Path.IsPathFullyQualified(d.Executable) || !d.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            problems.Add("Executable must be a full path to an .exe file (scripts and batch files are never run).");
        foreach (var list in new[] { d.VersionArguments, d.LaunchArguments })
            if (list.Count > 32 || list.Any(a => a is null || a.Length > 512 || a.IndexOf('\0') >= 0)) problems.Add("Arguments: at most 32, each up to 512 characters.");
        if (d.Environment.Count > 32) problems.Add("At most 32 environment variables.");
        foreach (var (k, v) in d.Environment)
        {
            if (!EnvName().IsMatch(k)) problems.Add($"Environment variable name '{k}' is not allowed.");
            if (k.Equals("PATH", StringComparison.OrdinalIgnoreCase) || k.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)) problems.Add($"'{k}' cannot be set.");
            if (LooksLikeSecret(v) || LooksLikeSecret(k + "=" + v)) problems.Add($"'{k}' looks like a secret. Secrets are never stored in definitions.");
        }
        if (d.LaunchArguments.Concat(d.VersionArguments).Any(LooksLikeSecret)) problems.Add("An argument looks like a secret. Secrets are never stored in definitions.");
        return problems;
    }

    public OperationResult Register(CustomAgentDefinition d)
    {
        var problems = Validate(d);
        if (problems.Count > 0) return OperationResult.Fail(string.Join(" ", problems));
        lock (_gate)
        {
            if (_all.Any(a => a.Id == d.Id)) return OperationResult.Fail("A custom tool with that id already exists.");
            _all.Add(d with { Trust = ToolTrust.Unverified, ValidatedVersion = null, ValidatedAt = null });
            Persist();
        }
        _audit.Record(new AuditEvent("CustomAgentRegistered", "CustomAgentRegistry", AuditResult.Success, d.Id, SecurityClassification.High, Detail: d.Executable));
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Registered as Unverified. Run its validation test before using it in a template.");
    }

    /// <summary>Marks a definition validated after its health check passed (called with the detected version).</summary>
    public OperationResult RecordValidation(string id, ToolStatus status)
    {
        lock (_gate)
        {
            var i = _all.FindIndex(a => a.Id == id);
            if (i < 0) return OperationResult.Fail("Unknown custom tool.");
            var passed = status.Installed && status.Health == ComponentHealth.Healthy && status.Version is not null;
            _all[i] = _all[i] with
            {
                Trust = passed ? ToolTrust.ValidatedLocally : ToolTrust.Unverified,
                ValidatedVersion = passed ? status.Version : null,
                ValidatedAt = passed ? DateTimeOffset.UtcNow : null,
            };
            Persist();
            _audit.Record(new AuditEvent("CustomAgentValidated", "CustomAgentRegistry", passed ? AuditResult.Success : AuditResult.Failure, id, SecurityClassification.Medium, Detail: status.Summary));
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return status.Health == ComponentHealth.Healthy ? OperationResult.Ok("Validated on this PC: " + status.Summary) : OperationResult.Fail("Validation failed: " + status.Summary);
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate) { removed = _all.RemoveAll(a => a.Id == id) > 0; if (removed) Persist(); }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    private void Persist() => _store.Save(StoreName, new Doc { Agents = _all });

    public sealed class Doc
    {
        public int SchemaVersion { get; set; } = 1;
        public List<CustomAgentDefinition> Agents { get; set; } = [];
    }
}
