using System.Text.Json;
using System.Text.Json.Serialization;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Stacks;

namespace DarkCipher.AgentControl.Application.Stacks;

/// <summary>Stack profiles: built-in templates plus user stacks. Every save/import is validated; imports are treated
/// as untrusted (size-capped, schema-checked, permission profile reset to the safe default, remote endpoints refused).</summary>
public sealed class StackService
{
    private const string StoreName = "stacks";
    private const int MaxImportBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, PropertyNameCaseInsensitive = true, MaxDepth = 16 };

    private readonly IDocumentStore _store;
    private readonly IAuditLogger _audit;
    private readonly IPermissionService _permissions;
    private readonly object _gate = new();
    private List<StackProfile> _user;

    public StackService(IDocumentStore store, IAuditLogger audit, IPermissionService permissions)
    {
        _store = store;
        _audit = audit;
        _permissions = permissions;
        _user = store.Load(StoreName, () => new StackDocument()).Stacks.Select(s => s with { BuiltIn = false }).ToList();
    }

    public event EventHandler? Changed;

    /// <summary>Extension-contributed templates (declarative only), registered by the extension host.</summary>
    private readonly List<StackProfile> _contributed = [];

    public IReadOnlyList<StackProfile> All
    {
        get { lock (_gate) return [.. StackTemplates.BuiltIn, .. _contributed, .. _user]; }
    }

    public StackProfile? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    public void RegisterContributed(StackProfile template, string extensionId)
    {
        var errors = Validate(template);
        if (errors.Count > 0) throw new InvalidOperationException("Invalid stack template: " + string.Join("; ", errors));
        lock (_gate)
        {
            _contributed.RemoveAll(s => s.Id == template.Id);
            _contributed.Add(template with { BuiltIn = true, TemplateSource = "extension:" + extensionId, PermissionProfileId = BuiltInPermissionProfiles.DefaultId });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public OperationResult Save(StackProfile stack)
    {
        if (StackTemplates.BuiltIn.Any(b => b.Id == stack.Id)) return OperationResult.Fail("Built-in stacks cannot be changed. Duplicate it first.");
        var errors = Validate(stack);
        if (errors.Count > 0) return OperationResult.Fail(string.Join(" ", errors));
        lock (_gate)
        {
            _user.RemoveAll(s => s.Id == stack.Id);
            _user.Add(stack with { BuiltIn = false, SchemaVersion = StackProfile.CurrentSchemaVersion });
            _store.Save(StoreName, new StackDocument { Stacks = [.. _user] });
        }
        _audit.Record(new AuditEvent("StackSaved", "StackService", AuditResult.Success, stack.Id, SecurityClassification.Low, Detail: $"{stack.AgentProviderId}/{stack.AgentMode}, permissions {stack.PermissionProfileId}"));
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Saved");
    }

    public StackProfile Duplicate(string id)
    {
        var src = Find(id) ?? throw new KeyNotFoundException("Stack not found.");
        var copy = src with { Id = NewId(src.Id), Name = src.Name + " (copy)", BuiltIn = false, TemplateSource = src.Id };
        var r = Save(copy);
        if (!r.Success) throw new InvalidOperationException(r.Message);
        return copy;
    }

    public OperationResult Delete(string id)
    {
        lock (_gate)
        {
            if (_user.RemoveAll(s => s.Id == id) == 0) return OperationResult.Fail("Only your own stacks can be deleted.");
            _store.Save(StoreName, new StackDocument { Stacks = [.. _user] });
        }
        _audit.Record(new AuditEvent("StackDeleted", "StackService", AuditResult.Success, id, SecurityClassification.Low));
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Deleted");
    }

    /// <summary>Exports a stack as JSON. Stacks never hold secrets, so nothing needs stripping; this is asserted by type.</summary>
    public string Export(string id) => JsonSerializer.Serialize(Find(id) ?? throw new KeyNotFoundException("Stack not found."), Json);

    public OperationResult<StackProfile> Import(string json)
    {
        if (json.Length > MaxImportBytes) return Reject("The file is too large to be a stack profile.");
        StackProfile? s;
        try { s = JsonSerializer.Deserialize<StackProfile>(json, Json); }
        catch (JsonException ex) { return Reject("Not a valid stack profile: " + ex.Message.Split('\n')[0]); }
        if (s is null) return Reject("Empty file.");
        if (s.SchemaVersion > StackProfile.CurrentSchemaVersion) return Reject($"This profile was made by a newer version (schema {s.SchemaVersion}).");

        var notices = new List<string>();
        if (s.PermissionProfileId != BuiltInPermissionProfiles.DefaultId)
            notices.Add($"Permission profile reset from '{s.PermissionProfileId}' to 'Ask Before Changes' — review before use.");
        if (s.Endpoint is { } ep && (!Uri.TryCreate(ep, UriKind.Absolute, out var u) || !u.IsLoopback))
            return Reject("Imported stacks may only use an endpoint on this PC. Remote endpoints must be added manually.");
        var imported = s with
        {
            Id = Find(s.Id) is null ? s.Id : NewId(s.Id),
            BuiltIn = false,
            TemplateSource = "import",
            PermissionProfileId = BuiltInPermissionProfiles.DefaultId,
            Privacy = s.AgentMode is AgentMode.Native ? s.Privacy : PrivacyMode.FullyLocal,
        };
        var errors = Validate(imported);
        if (errors.Count > 0) return Reject(string.Join(" ", errors));
        var saved = Save(imported);
        if (!saved.Success) return Reject(saved.Message);
        _audit.Record(new AuditEvent("StackImported", "StackService", AuditResult.Success, imported.Id, SecurityClassification.Medium, Detail: string.Join(" ", notices)));
        return OperationResult<StackProfile>.Ok(imported, notices.Count == 0 ? "Imported." : "Imported. " + string.Join(" ", notices));
    }

    private OperationResult<StackProfile> Reject(string why)
    {
        _audit.Record(new AuditEvent("StackImportRejected", "StackService", AuditResult.Blocked, null, SecurityClassification.Medium, Detail: why));
        return OperationResult<StackProfile>.Fail(why);
    }

    public List<string> Validate(StackProfile s)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 64 || !s.Id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) e.Add("Id must be 1–64 letters, digits or '-'.");
        if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 80) e.Add("Name must be 1–80 characters.");
        if (s.Description.Length > 500) e.Add("Description is too long.");
        if (string.IsNullOrWhiteSpace(s.AgentProviderId) || s.AgentProviderId.Length > 64) e.Add("Choose an agent.");
        foreach (var (label, m) in new[] { ("Primary model", s.PrimaryModel), ("Fallback model", s.FallbackModel), ("Utility model", s.UtilityModel) })
            if (m is not null && !ModelNameRules.IsValid(m)) e.Add($"{label} name is not valid.");
        if (s.Endpoint is { } ep && (!Uri.TryCreate(ep, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || !string.IsNullOrEmpty(u.UserInfo)))
            e.Add("Endpoint must be an http(s) URL without embedded credentials.");
        if (s.ContextLength is < 2048 or > 1_048_576) e.Add("Context must be between 2,048 and 1,048,576 tokens.");
        if (_permissions.Profiles.All(p => p.Id != s.PermissionProfileId)) e.Add("Unknown permission profile.");
        if (s.AgentMode is AgentMode.LocalProvider or AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway && s.PrimaryModel is null)
            e.Add("Local stacks need a primary model.");
        if (s.Options is { } opts)
            foreach (var (k, v) in opts)
                if (!TemplateOptions.Allowed.TryGetValue(k, out var allowed) || !allowed.Contains(v)) e.Add($"Option '{k}={v}' is not allowed.");
        if (s.SystemInstructions is { } si && (si.Length > TemplateOptions.MaxSystemInstructions || Tools.CustomAgentRegistry.LooksLikeSecret(si)))
            e.Add("Instructions are too long or contain something that looks like a secret.");
        return e;
    }

    private string NewId(string baseId)
    {
        var stem = new string(baseId.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(50).ToArray());
        for (var i = 2; ; i++)
        {
            var id = $"{stem}-{i}";
            if (Find(id) is null) return id;
        }
    }

    public sealed class StackDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<StackProfile> Stacks { get; set; } = [];
    }
}
