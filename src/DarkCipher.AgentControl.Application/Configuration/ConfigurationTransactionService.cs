using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Configuration;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Application.Configuration;

/// <summary>Who wrote which value last (per file, per key). Used to tell an app-written value from one the user changed.</summary>
public sealed class ConfigurationOwnershipLedger
{
    private const string StoreName = "config-ownership";
    private readonly IDocumentStore _store;
    private readonly object _gate = new();
    private readonly Doc _doc;

    public ConfigurationOwnershipLedger(IDocumentStore store)
    {
        _store = store;
        _doc = store.Load(StoreName, () => new Doc());
    }

    public sealed record Entry(ConfigOwnership Owner, string LastWrittenValue, DateTimeOffset At);

    public sealed class Doc
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, Dictionary<string, Entry>> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, Entry> For(string canonicalPath)
    {
        lock (_gate) return _doc.Files.TryGetValue(canonicalPath, out var d) ? new Dictionary<string, Entry>(d) : new Dictionary<string, Entry>();
    }

    public void Record(string canonicalPath, IEnumerable<ConfigValue> written)
    {
        lock (_gate)
        {
            if (!_doc.Files.TryGetValue(canonicalPath, out var d)) _doc.Files[canonicalPath] = d = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var v in written) d[v.Key] = new Entry(v.Owner, v.Value, DateTimeOffset.UtcNow);
            _store.Save(StoreName, _doc);
        }
    }

    public void Release(string canonicalPath, IEnumerable<string> keys)
    {
        lock (_gate)
        {
            if (_doc.Files.TryGetValue(canonicalPath, out var d)) foreach (var k in keys) d.Remove(k);
            _store.Save(StoreName, _doc);
        }
    }
}

/// <summary>What a transaction would do, before anything is written (steps 1-7).</summary>
public sealed record TransactionPreview(
    string TargetPath,
    ConfigFormat Format,
    string Purpose,
    bool TargetExists,
    string? CurrentContent,
    string ProposedContent,
    IReadOnlyList<string> Diff,
    IReadOnlyList<ConfigValue> Changes,
    IReadOnlyList<ConfigConflict> Conflicts,
    IReadOnlyList<TransactionStep> Steps)
{
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Configuration transactions for files the app does not fully own. The pipeline follows the specification:
/// discover → parse → validate → (backup) → build the proposed file by MERGING only the intended keys (unrelated settings,
/// comments and order are kept) → show the change → validate syntax → write atomically with a backup → re-read → confirm
/// the intended values → optional reload/health/integration checks → commit; any failure after the write rolls back.
/// Values the user changed after the app last wrote them are reported as conflicts and are never overwritten without a
/// decision.
/// </summary>
public sealed class ConfigurationTransactionService
{
    private const string HistoryStore = "config-transactions";
    private readonly IConfigurationProtection _files;
    private readonly IProtectedPathService _paths;
    private readonly ConfigurationOwnershipLedger _ledger;
    private readonly IDocumentStore _store;
    private readonly IAuditLogger _audit;
    private readonly object _gate = new();
    private readonly List<ConfigTransactionRecord> _history;

    public ConfigurationTransactionService(IConfigurationProtection files, IProtectedPathService paths, ConfigurationOwnershipLedger ledger, IDocumentStore store, IAuditLogger audit)
    {
        _files = files;
        _paths = paths;
        _ledger = ledger;
        _store = store;
        _audit = audit;
        _history = store.Load(HistoryStore, () => new HistoryDoc()).Transactions;
    }

    public IReadOnlyList<ConfigTransactionRecord> History { get { lock (_gate) return _history.OrderByDescending(t => t.At).ToList(); } }

    public sealed class HistoryDoc
    {
        public int SchemaVersion { get; set; } = 1;
        public List<ConfigTransactionRecord> Transactions { get; set; } = [];
    }

    /// <summary>Steps 1-7: nothing is written.</summary>
    public TransactionPreview Prepare(string targetPath, ConfigFormat format, IReadOnlyList<ConfigValue> desired, string purpose)
    {
        var steps = new List<TransactionStep>();
        var canonical = _paths.Canonicalize(targetPath);
        var current = _files.Read(canonical);
        steps.Add(new("Discover", true, current is null ? "The file does not exist yet; it will be created." : $"Found ({current.Length:N0} characters)."));

        IConfigDocument doc;
        try { doc = ConfigDocuments.Parse(format, current); }
        catch (ConfigFormatException ex)
        {
            steps.Add(new("Parse and validate current file", false, ex.Message + " The file was not changed."));
            return new TransactionPreview(canonical, format, purpose, current is not null, current, current ?? "", [], [], [], steps);
        }
        steps.Add(new("Parse and validate current file", true, $"{doc.Values.Count} setting(s) read; all will be kept unless listed below."));

        var owned = _ledger.For(canonical);
        var existing = doc.Values;
        var changes = new List<ConfigValue>();
        var conflicts = new List<ConfigConflict>();
        foreach (var v in desired)
        {
            existing.TryGetValue(v.Key, out var now);
            if (now == v.Value) continue;
            // A value differs from what this app last wrote → the user changed it. With no record, a value in a file the app
            // does not manage is the user's (External) and is never overwritten without a decision.
            var userChanged = now is not null && (owned.TryGetValue(v.Key, out var e) ? e.LastWrittenValue != now : !ManagedFile(current));
            if (userChanged)
            {
                conflicts.Add(new ConfigConflict(v.Key, now!, v.Value, owned.TryGetValue(v.Key, out var w) ? w.LastWrittenValue : "(never written by this app)"));
                continue;
            }
            changes.Add(v);
        }
        foreach (var c in changes) doc.Set(c.Key, c.Value);
        var proposed = changes.Count == 0 ? current ?? "" : doc.Render();
        steps.Add(new("Build proposed configuration", true, changes.Count == 0 ? "No change needed." : $"{changes.Count} value(s) to set; {conflicts.Count} conflict(s) need a decision."));

        try { ConfigDocuments.Parse(format, proposed); steps.Add(new("Validate syntax", true, "The proposed file parses.")); }
        catch (ConfigFormatException ex) { steps.Add(new("Validate syntax", false, ex.Message)); }

        var diff = changes.Count == 0 ? [] : _files.Preview(canonical, proposed).DiffLines;
        return new TransactionPreview(canonical, format, purpose, current is not null, current, proposed, diff, changes, conflicts, steps);
    }

    /// <summary>
    /// For a file generated by a typed builder (e.g. an isolated Codex profile): a new file is created with the generated
    /// content as is (header comment included); an existing file has only the generated keys merged in.
    /// </summary>
    public TransactionPreview PrepareFromGenerated(string targetPath, ConfigFormat format, string generatedContent, string purpose, ConfigOwnership owner = ConfigOwnership.ApplicationManaged)
    {
        var generated = ConfigDocuments.Parse(format, generatedContent).Values.Select(kv => new ConfigValue(kv.Key, kv.Value, owner)).ToList();
        var preview = Prepare(targetPath, format, generated, purpose);
        if (preview.TargetExists || preview.Steps.Any(s => !s.Ok)) return preview;
        return preview with { ProposedContent = generatedContent, Diff = _files.Preview(preview.TargetPath, generatedContent).DiffLines, Changes = generated };
    }

    /// <summary>Applies decisions on conflicts to a preview (re-prepares with the user's choice).</summary>
    public TransactionPreview Resolve(TransactionPreview preview, IReadOnlyList<ConfigValue> desired, IReadOnlyDictionary<string, ConflictDecision> decisions)
    {
        // Values the user chose to overwrite are marked as acknowledged by recording the current value as last-written,
        // which turns the conflict into an ordinary change on the next Prepare.
        var take = preview.Conflicts.Where(c => decisions.TryGetValue(c.Key, out var d) && d == ConflictDecision.UseGeneratedValue).ToList();
        if (take.Count > 0) _ledger.Record(preview.TargetPath, take.Select(c => new ConfigValue(c.Key, c.CurrentValue, ConfigOwnership.UserManaged)));
        var kept = preview.Conflicts.Where(c => decisions.TryGetValue(c.Key, out var d) && d == ConflictDecision.KeepUserValue).Select(c => c.Key).ToHashSet();
        if (kept.Count > 0) _ledger.Release(preview.TargetPath, kept);
        return Prepare(preview.TargetPath, preview.Format, desired.Where(v => !kept.Contains(v.Key)).ToList(), preview.Purpose);
    }

    /// <summary>Steps 8-15. <paramref name="healthCheck"/> and <paramref name="integrationTest"/> run after the write; a
    /// false result (or exception) rolls the file back.</summary>
    public async Task<ConfigTransactionRecord> CommitAsync(TransactionPreview preview, Func<CancellationToken, Task<(bool Ok, string Detail)>>? healthCheck,
        Func<CancellationToken, Task<(bool Ok, string Detail)>>? integrationTest, CancellationToken ct)
    {
        var correlation = CorrelationId.New();
        var steps = preview.Steps.ToList();
        var record = new ConfigTransactionRecord
        {
            Id = correlation.Value, TargetPath = preview.TargetPath, Purpose = preview.Purpose, TargetExisted = preview.TargetExists,
            ChangedKeys = preview.Changes.Select(c => c.Key).ToList(), Correlation = correlation.Value, State = TransactionState.Planned,
        };
        ConfigTransactionRecord Done(TransactionState state) { var r = record with { State = state, Steps = steps }; Save(r); return r; }

        if (steps.Any(s => !s.Ok)) { steps.Add(new("Commit", false, "Not written: a validation step failed.")); return Done(TransactionState.Failed); }
        if (preview.Conflicts.Count > 0) { steps.Add(new("Commit", false, "Not written: resolve the conflicts first.")); return Done(TransactionState.Failed); }
        if (!preview.HasChanges) { steps.Add(new("Commit", true, "Nothing to change.")); return Done(TransactionState.Committed); }

        // Apply re-checks that the file did not change since the preview, takes a backup and writes atomically.
        var filePreview = _files.Preview(preview.TargetPath, preview.ProposedContent);
        if (filePreview.CurrentContent != preview.CurrentContent) { steps.Add(new("Back up and write atomically", false, "The file changed after the preview. Prepare again.")); return Done(TransactionState.Failed); }
        var applied = _files.Apply(filePreview, correlation);
        if (!applied.Success) { steps.Add(new("Back up and write atomically", false, applied.Message)); return Done(TransactionState.Failed); }
        var backup = applied.Value;
        record = record with { BackupPath = backup?.BackupPath, State = TransactionState.Applied };
        steps.Add(new("Back up and write atomically", true, backup is null ? "Created." : "Previous version backed up."));

        async Task<ConfigTransactionRecord> RollBack(string why)
        {
            var r = backup is not null ? _files.Rollback(backup, correlation) : _files.RemoveCreated(preview.TargetPath, preview.ProposedContent, correlation);
            steps.Add(new("Roll back", r.Success, r.Success ? why + " The previous file was restored." : why + " Rollback problem: " + r.Message));
            _audit.Record(new AuditEvent("ConfigTransactionRolledBack", "ConfigurationTransactionService", r.Success ? AuditResult.Success : AuditResult.Failure,
                preview.TargetPath, SecurityClassification.Medium, correlation, why));
            await Task.CompletedTask.ConfigureAwait(false);
            return Done(r.Success ? TransactionState.RolledBack : TransactionState.Failed);
        }

        var reread = _files.Read(preview.TargetPath);
        if (reread != preview.ProposedContent) return await RollBack("Re-read did not match what was written.").ConfigureAwait(false);
        steps.Add(new("Re-read", true, "The file on disk matches the proposed content."));
        var values = ConfigDocuments.Parse(preview.Format, reread).Values;
        var wrong = preview.Changes.Where(c => !values.TryGetValue(c.Key, out var v) || v != c.Value).Select(c => c.Key).ToList();
        if (wrong.Count > 0) return await RollBack("Intended values not found after writing: " + string.Join(", ", wrong) + ".").ConfigureAwait(false);
        steps.Add(new("Confirm intended values", true, string.Join(", ", preview.Changes.Select(c => c.Key))));
        record = record with { State = TransactionState.Verified };

        foreach (var (name, check) in new[] { ("Health check", healthCheck), ("Integration test", integrationTest) })
        {
            if (check is null) { steps.Add(new(name, true, "Not applicable.")); continue; }
            (bool Ok, string Detail) r;
            try { r = await check(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { r = (false, ex.Message); }
            steps.Add(new(name, r.Ok, r.Detail));
            if (!r.Ok) return await RollBack($"{name} failed: {r.Detail}").ConfigureAwait(false);
        }

        _ledger.Record(preview.TargetPath, preview.Changes);
        steps.Add(new("Commit", true, "Committed; ownership recorded."));
        _audit.Record(new AuditEvent("ConfigTransactionCommitted", "ConfigurationTransactionService", AuditResult.Success, preview.TargetPath, SecurityClassification.Medium, correlation,
            string.Join(", ", preview.Changes.Select(c => c.Key))));
        return Done(TransactionState.Committed);
    }

    /// <summary>"Roll back last change" for a file: restores the backup taken by its last committed transaction.</summary>
    public OperationResult RollBackLast(string targetPath)
    {
        var canonical = _paths.Canonicalize(targetPath);
        ConfigTransactionRecord? last;
        lock (_gate) last = _history.Where(t => t.TargetPath.Equals(canonical, StringComparison.OrdinalIgnoreCase) && t.State == TransactionState.Committed && t.ChangedKeys.Count > 0)
            .OrderByDescending(t => t.At).FirstOrDefault();
        if (last is null) return OperationResult.Fail("No committed change to roll back for this file.");
        var correlation = CorrelationId.New();
        OperationResult r;
        if (last.BackupPath is not null)
            r = _files.Rollback(_files.Backups(canonical).FirstOrDefault(b => b.BackupPath.Equals(last.BackupPath, StringComparison.OrdinalIgnoreCase))
                ?? new ConfigBackup(canonical, last.BackupPath, last.At, 0), correlation);
        else
        {
            var content = _files.Read(canonical);
            r = content is null ? OperationResult.Ok("Already gone.") : _files.RemoveCreated(canonical, content, correlation);
        }
        if (r.Success)
        {
            _ledger.Release(canonical, last.ChangedKeys);
            Save(last with { State = TransactionState.RolledBack, Steps = [.. last.Steps, new TransactionStep("Roll back (by you)", true, r.Message)] });
        }
        return r;
    }

    private static bool ManagedFile(string? content) => content is not null && content.Contains("Managed by DarkCipher AgentControl", StringComparison.Ordinal);

    private void Save(ConfigTransactionRecord r)
    {
        lock (_gate)
        {
            var i = _history.FindIndex(t => t.Id == r.Id);
            if (i >= 0) _history[i] = r; else _history.Add(r);
            if (_history.Count > 200) _history.RemoveRange(0, _history.Count - 200);
            _store.Save(HistoryStore, new HistoryDoc { Transactions = _history });
        }
    }
}
