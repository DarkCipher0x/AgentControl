using System.Text.Json;
using System.Text.Json.Nodes;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.Application.Templates;

public sealed record TemplateImportPreview(bool Valid, TemplateDefinition? Template, IReadOnlyList<string> Notes, IReadOnlyList<TemplateValidationIssue> Issues, string Message);

/// <summary>
/// Built-in, extension-provided, user-created and imported templates. Built-in and extension templates are read-only
/// (duplicate to change them). Every save, import and extension registration is validated; imports are untrusted
/// (size-capped, unknown fields refused, secret scan, migrated, permission profile reset to the safe default).
/// </summary>
public sealed class TemplateCatalogueService
{
    private const string StoreName = "templates";
    private const int MaxImportBytes = 64 * 1024;
    private readonly IDocumentStore _store;
    private readonly IAuditLogger _audit;
    private readonly TemplateValidationService _validation;
    private readonly object _gate = new();
    private readonly List<TemplateDefinition> _user;
    private readonly List<TemplateDefinition> _extension = [];

    public TemplateCatalogueService(IDocumentStore store, IAuditLogger audit, TemplateValidationService validation)
    {
        _store = store;
        _audit = audit;
        _validation = validation;
        _user = store.Load(StoreName, () => new Doc()).Templates
            .Where(t => validation.Validate(t).Count == 0 && BuiltInTemplates.All.All(b => b.Id != t.Id)).ToList();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<TemplateDefinition> All { get { lock (_gate) return [.. BuiltInTemplates.All, .. _extension, .. _user]; } }

    public TemplateDefinition? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    public bool IsReadOnly(string id) => BuiltInTemplates.All.Any(t => t.Id == id) || _extension.Any(t => t.Id == id);

    public OperationResult RegisterExtensionTemplate(TemplateDefinition t, string extensionId)
    {
        var issues = _validation.Validate(t);
        if (issues.Count > 0) return OperationResult.Fail("Invalid template: " + string.Join("; ", issues.Select(i => $"{i.Field}: {i.Problem}")));
        if (BuiltInTemplates.All.Any(b => b.Id == t.Id)) return OperationResult.Fail("An extension cannot replace a built-in template.");
        lock (_gate)
        {
            _extension.RemoveAll(x => x.Id == t.Id);
            // Extensions never choose permissions: their templates always start from the safe default.
            _extension.Add(t with { Origin = TemplateOrigin.Extension, Author = extensionId, PermissionProfileId = Domain.Agents.BuiltInPermissionProfiles.DefaultId });
        }
        _audit.Record(new AuditEvent("TemplateRegistered", "TemplateCatalogueService", AuditResult.Success, t.Id, SecurityClassification.Low, Detail: "extension " + extensionId));
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Registered.");
    }

    public OperationResult Save(TemplateDefinition t)
    {
        if (IsReadOnly(t.Id)) return OperationResult.Fail("Built-in and extension templates cannot be changed. Duplicate it first.");
        var issues = _validation.Validate(t);
        if (issues.Count > 0) return OperationResult.Fail(string.Join(" ", issues.Select(i => $"{i.Field}: {i.Problem}")));
        lock (_gate)
        {
            _user.RemoveAll(x => x.Id == t.Id);
            _user.Add(t with { Origin = t.Origin == TemplateOrigin.Imported ? TemplateOrigin.Imported : TemplateOrigin.User, Author = "user" });
            Persist();
        }
        _audit.Record(new AuditEvent("TemplateSaved", "TemplateCatalogueService", AuditResult.Success, t.Id, SecurityClassification.Low, Detail: t.Version));
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Saved.");
    }

    public OperationResult<TemplateDefinition> Duplicate(string id)
    {
        var src = Find(id);
        if (src is null) return OperationResult<TemplateDefinition>.Fail("Template not found.");
        var copy = src with { Id = NewId(src.Id), DisplayName = Trim(src.DisplayName + " (copy)"), Origin = TemplateOrigin.User, DerivedFrom = src.Id, Version = "1.0.0" };
        var r = Save(copy);
        return r.Success ? OperationResult<TemplateDefinition>.Ok(copy, "Duplicated.") : OperationResult<TemplateDefinition>.Fail(r.Message);
    }

    public OperationResult Delete(string id)
    {
        lock (_gate)
        {
            if (_user.RemoveAll(t => t.Id == id) == 0) return OperationResult.Fail("Only your own templates can be deleted.");
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Deleted.");
    }

    /// <summary>Restores a duplicated template from its source (keeps its own id and name).</summary>
    public OperationResult ResetToDefault(string id)
    {
        var mine = _user.FirstOrDefault(t => t.Id == id);
        if (mine?.DerivedFrom is null || Find(mine.DerivedFrom) is not { } source) return OperationResult.Fail("This template has no default to return to.");
        return Save(source with { Id = mine.Id, DisplayName = mine.DisplayName, Origin = TemplateOrigin.User, DerivedFrom = mine.DerivedFrom, Version = Bump(mine.Version) });
    }

    /// <summary>JSON export (schema 2). Templates hold no secrets by construction; the export is scanned anyway.</summary>
    public OperationResult<string> Export(string id)
    {
        var t = Find(id);
        if (t is null) return OperationResult<string>.Fail("Template not found.");
        var json = JsonSerializer.Serialize(t with { Author = null }, TemplateValidationService.Json);
        if (TemplateValidationService.ContainsSecret(json)) return OperationResult<string>.Fail("The template appears to contain a secret; export refused.");
        return OperationResult<string>.Ok(json);
    }

    public TemplateImportPreview PreviewImport(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(false, null, [], [], "The file is empty.");
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxImportBytes) return new(false, null, [], [], "The file is too large to be a template.");
        if (TemplateValidationService.ContainsSecret(json)) return Reject("The file contains something that looks like a credential. Templates never contain secrets; import refused.");
        JsonNode? node;
        try { node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException ex) { return Reject("Not valid JSON: " + ex.Message.Split('\n')[0]); }
        var migrated = TemplateMigrationService.Migrate(node);
        if (migrated.Template is null) return Reject(migrated.Error ?? "Not a template.");
        var notes = migrated.Notes.ToList();
        var t = migrated.Template with { Origin = TemplateOrigin.Imported, Author = "import", DerivedFrom = null };
        if (t.PermissionProfileId != Domain.Agents.BuiltInPermissionProfiles.DefaultId)
        {
            notes.Add($"Permission profile '{t.PermissionProfileId}' replaced by 'Ask Before Changes' — review before use.");
            t = t with { PermissionProfileId = Domain.Agents.BuiltInPermissionProfiles.DefaultId };
        }
        if (Find(t.Id) is not null) { var id = NewId(t.Id); notes.Add($"Id '{t.Id}' exists; imported as '{id}'."); t = t with { Id = id }; }
        var issues = _validation.Validate(t);
        return new(issues.Count == 0, t, notes, issues, issues.Count == 0 ? "Valid. Review, then import." : "The template is not valid.");

        TemplateImportPreview Reject(string why)
        {
            _audit.Record(new AuditEvent("TemplateImportRejected", "TemplateCatalogueService", AuditResult.Blocked, null, SecurityClassification.Medium, Detail: why));
            return new(false, null, [], [], why);
        }
    }

    public OperationResult ApplyImport(TemplateImportPreview preview)
    {
        if (!preview.Valid || preview.Template is null) return OperationResult.Fail("Nothing valid to import.");
        var r = Save(preview.Template);
        if (r.Success) _audit.Record(new AuditEvent("TemplateImported", "TemplateCatalogueService", AuditResult.Success, preview.Template.Id, SecurityClassification.Medium, Detail: string.Join(" ", preview.Notes)));
        return r;
    }

    private string NewId(string baseId)
    {
        var stem = baseId.Length > 56 ? baseId[..56] : baseId;
        for (var i = 2; ; i++) { var id = $"{stem}-{i}"; if (Find(id) is null) return id; }
    }

    private static string Trim(string s) => s.Length <= 80 ? s : s[..80];

    private static string Bump(string v)
    {
        var p = v.Split('.');
        return p.Length == 3 && int.TryParse(p[2], out var patch) ? $"{p[0]}.{p[1]}.{patch + 1}" : "1.0.1";
    }

    private void Persist() => _store.Save(StoreName, new Doc { Templates = _user });

    public sealed class Doc
    {
        public int SchemaVersion { get; set; } = TemplateDefinition.CurrentSchemaVersion;
        public List<TemplateDefinition> Templates { get; set; } = [];
    }
}
