using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Sessions;

namespace DarkCipher.AgentControl.Application.Sessions;

/// <summary>Projects the user has explicitly selected. Paths are stored canonical; protected paths are explained at
/// add time and re-evaluated at every launch.</summary>
public sealed class ProjectService
{
    private const string StoreName = "projects";
    private readonly IDocumentStore _store;
    private readonly IProtectedPathService _paths;
    private readonly IAuditLogger _audit;
    private readonly object _gate = new();
    private readonly List<ProjectEntry> _projects;

    public ProjectService(IDocumentStore store, IProtectedPathService paths, IAuditLogger audit)
    {
        _store = store;
        _paths = paths;
        _audit = audit;
        _projects = store.Load(StoreName, () => new Doc()).Projects;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ProjectEntry> All { get { lock (_gate) return [.. _projects]; } }

    public ProjectEntry? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public OperationResult<ProjectEntry> Add(string folder, string? name = null)
    {
        string canonical;
        try { canonical = _paths.Canonicalize(folder); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return OperationResult<ProjectEntry>.Fail(ex.Message); }
        if (!Directory.Exists(canonical)) return OperationResult<ProjectEntry>.Fail("That folder does not exist.");

        var decision = _paths.Evaluate(canonical, PathOperation.UseAsWorkingDirectory, PathActor.Agent);
        if (decision.Kind == PathDecisionKind.Deny) return OperationResult<ProjectEntry>.Fail("This folder cannot be used as a project: " + decision.Reason);

        lock (_gate)
        {
            var existing = _projects.FirstOrDefault(p => string.Equals(p.Path, canonical, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return OperationResult<ProjectEntry>.Ok(existing, "Already added.");
            var entry = new ProjectEntry(Guid.NewGuid().ToString("N")[..10], string.IsNullOrWhiteSpace(name) ? Path.GetFileName(canonical) : name.Trim(), canonical, DateTimeOffset.UtcNow);
            _projects.Add(entry);
            Persist();
            _audit.Record(new AuditEvent("ProjectAdded", "ProjectService", AuditResult.Success, canonical, SecurityClassification.Low,
                Detail: decision.Kind == PathDecisionKind.RequiresApproval ? decision.Reason : null));
            Changed?.Invoke(this, EventArgs.Empty);
            return OperationResult<ProjectEntry>.Ok(entry, decision.Kind == PathDecisionKind.RequiresApproval ? "Added. Note: " + decision.Reason : "Added.");
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (_projects.RemoveAll(p => p.Id == id) == 0) return false;
            Persist();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true; // removes the entry only; the folder on disk is never touched
    }

    public PathDecision Check(ProjectEntry p) => _paths.Evaluate(p.Path, PathOperation.UseAsWorkingDirectory, PathActor.Agent);

    private void Persist() => _store.Save(StoreName, new Doc { Projects = _projects });

    public sealed class Doc
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ProjectEntry> Projects { get; set; } = [];
    }
}
