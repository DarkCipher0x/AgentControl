using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Application.Downloads;

/// <summary>One queue item (model pull, Hugging Face import, tool install/update/repair/uninstall) with live progress.</summary>
public sealed class DownloadJob : INotifyPropertyChanged
{
    private DownloadPhase _phase = DownloadPhase.Queued;
    private string _status = "Queued";
    private long? _done, _total;
    private double _bytesPerSecond;
    private string? _error, _verification, _finalLocation;
    private DateTimeOffset? _finishedAt;
    private readonly List<string> _log = [];

    public DownloadJob(DownloadRequest request, string? id = null, DateTimeOffset? queuedAt = null)
    {
        Request = request;
        Id = id ?? Guid.NewGuid().ToString("N")[..10];
        QueuedAt = queuedAt ?? DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public DownloadRequest Request { get; }
    public DateTimeOffset QueuedAt { get; }
    internal CancellationTokenSource? Cts { get; set; }
    internal bool PauseRequested { get; set; }

    public DownloadPhase Phase { get => _phase; internal set => Set(ref _phase, value); }
    public string Status { get => _status; internal set { Set(ref _status, value); AddLog(value); } }
    public long? BytesDone { get => _done; internal set { Set(ref _done, value); OnChanged(nameof(Percent)); OnChanged(nameof(Eta)); } }
    public long? BytesTotal { get => _total; internal set { Set(ref _total, value); OnChanged(nameof(Percent)); } }
    public double BytesPerSecond { get => _bytesPerSecond; internal set { Set(ref _bytesPerSecond, value); OnChanged(nameof(Eta)); OnChanged(nameof(SpeedText)); } }
    public string? Error { get => _error; internal set { Set(ref _error, value); if (value is not null) AddLog("Error: " + value); } }
    /// <summary>What the Testing stage proved (or why it failed).</summary>
    public string? VerificationResult { get => _verification; internal set => Set(ref _verification, value); }
    public string? FinalLocation { get => _finalLocation; internal set => Set(ref _finalLocation, value); }
    public DateTimeOffset? FinishedAt { get => _finishedAt; internal set => Set(ref _finishedAt, value); }
    public IReadOnlyList<string> Log { get { lock (_log) return [.. _log]; } }

    public double? Percent => BytesTotal is > 0 && BytesDone is not null ? 100.0 * BytesDone.Value / BytesTotal.Value : null;
    public bool IsIndeterminate => IsActive && Percent is null;

    public TimeSpan? Eta => BytesPerSecond > 1 && BytesTotal is > 0 && BytesDone is not null
        ? TimeSpan.FromSeconds((BytesTotal.Value - BytesDone.Value) / BytesPerSecond) : null;

    public string SpeedText => BytesPerSecond > 1 ? Bytes.Format((long)BytesPerSecond) + "/s" : "";

    public bool IsActive => Phase is DownloadPhase.Resolving or DownloadPhase.Preparing or DownloadPhase.Downloading or DownloadPhase.Verifying
        or DownloadPhase.Importing or DownloadPhase.Installing or DownloadPhase.Configuring or DownloadPhase.Testing or DownloadPhase.Completed;
    public bool IsFinished => Phase is DownloadPhase.Ready or DownloadPhase.ReadyWithLimitations or DownloadPhase.Failed or DownloadPhase.Cancelled
        or DownloadPhase.RollbackRequired or DownloadPhase.Interrupted;
    public bool IsFailed => Phase is DownloadPhase.Failed or DownloadPhase.RollbackRequired or DownloadPhase.Interrupted;
    public bool IsPaused => Phase == DownloadPhase.Paused;

    public string TypeLabel => Request.Kind switch
    {
        QueueItemKind.ModelPull => "Model (Ollama)",
        QueueItemKind.HuggingFaceImport => "Model (Hugging Face → Ollama)",
        QueueItemKind.GgufDownload => "Model file (GGUF)",
        QueueItemKind.AgentTool => "Agent tool",
        QueueItemKind.ModelRuntime => "Model runtime",
        QueueItemKind.Gateway => "Gateway",
        QueueItemKind.Extension => "Extension",
        QueueItemKind.TemplatePackage => "Template package",
        _ => Request.Kind.ToString(),
    };

    /// <summary>The stage as the specification names it (text, never colour alone).</summary>
    public string StageLabel => Phase switch
    {
        DownloadPhase.Preparing => "Resolving",
        DownloadPhase.Importing => "Installing",
        DownloadPhase.Completed => "Testing",
        DownloadPhase.ReadyWithLimitations => "Ready with limitations",
        DownloadPhase.RollbackRequired => "Rollback required",
        _ => Phase.ToString(),
    };

    public string InstallState => Phase switch
    {
        DownloadPhase.Ready => "Installed and verified",
        DownloadPhase.ReadyWithLimitations => "Installed (limited)",
        DownloadPhase.RollbackRequired => "Installed but failed verification",
        DownloadPhase.Installing or DownloadPhase.Importing or DownloadPhase.Configuring => "Installing",
        DownloadPhase.Testing or DownloadPhase.Completed => "Installed, testing",
        _ => "Not installed",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void AddLog(string line)
    {
        lock (_log)
        {
            if (_log.Count > 0 && _log[^1].EndsWith(line, StringComparison.Ordinal)) return;
            _log.Add($"{DateTimeOffset.Now:HH:mm:ss}  {line}");
            if (_log.Count > 400) _log.RemoveRange(0, _log.Count - 400);
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name!);
        if (name == nameof(Phase))
        {
            foreach (var n in new[] { nameof(IsActive), nameof(IsFinished), nameof(IsFailed), nameof(IsPaused), nameof(StageLabel), nameof(InstallState), nameof(IsIndeterminate) })
                OnChanged(n);
        }
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The unified download and installation queue (models, Hugging Face imports, agent tools, runtimes). Sequential, with
/// de-duplication by target, a free-disk check before starting, cancel / retry / pause-and-resume (only where the provider
/// can resume), live bytes/speed/ETA, and a Testing stage: an item becomes Ready only after its verifier proved it works.
/// Queue history survives restarts; an item that was running when the app closed is marked Interrupted, never resumed
/// silently. Failures are reported on the item, logged and audited — never silent.
/// </summary>
public sealed class DownloadManager
{
    private const string StoreName = "download-queue";
    private readonly IReadOnlyDictionary<string, IDownloadProvider> _providers;
    private readonly IReadOnlyList<IInstalledItemVerifier> _verifiers;
    private readonly IAppLogger _log;
    private readonly IAuditLogger _audit;
    private readonly IDocumentStore? _store;
    private readonly Func<long?> _freeDiskBytes;
    private readonly object _gate = new();
    private readonly List<DownloadJob> _jobs = [];
    private readonly SemaphoreSlim _worker = new(1, 1);
    private Action<Action> _dispatch = a => a();

    public DownloadManager(IEnumerable<IDownloadProvider> providers, IAppLogger log, IAuditLogger audit, Func<long?> freeDiskBytes,
        IEnumerable<IInstalledItemVerifier>? verifiers = null, IDocumentStore? store = null)
    {
        _providers = providers.ToDictionary(p => p.Id);
        _verifiers = verifiers?.ToList() ?? [];
        _log = log;
        _audit = audit;
        _freeDiskBytes = freeDiskBytes;
        _store = store;
        if (store is not null) Restore(store);
    }

    /// <summary>The UI sets a dispatcher so job property changes are raised on its thread.</summary>
    public void SetDispatcher(Action<Action> dispatch) => _dispatch = dispatch;

    public event EventHandler? Changed;

    public IReadOnlyList<DownloadJob> Jobs { get { lock (_gate) return [.. _jobs]; } }

    public bool CanPause(DownloadJob job) => job.IsActive && _providers.TryGetValue(job.Request.ProviderId, out var p) && p.SupportsResume
        && job.Phase is DownloadPhase.Downloading or DownloadPhase.Preparing;

    public OperationResult<DownloadJob> Enqueue(DownloadRequest request)
    {
        if (!_providers.ContainsKey(request.ProviderId)) return OperationResult<DownloadJob>.Fail($"No download provider '{request.ProviderId}'.");
        lock (_gate)
        {
            var existing = _jobs.FirstOrDefault(j => !j.IsFinished && string.Equals(j.Request.Target, request.Target, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return OperationResult<DownloadJob>.Ok(existing, "Already queued.");
        }
        if (request.ExpectedBytes is { } need && _freeDiskBytes() is { } free && free < need + need / 10)
            return OperationResult<DownloadJob>.Fail($"Not enough free disk space: about {Bytes.Format(need)} needed, {Bytes.Format(free)} free.");

        var job = new DownloadJob(request);
        job.AddLog($"Queued: {request.DisplayName} ({request.Kind}) from {(request.Source.Length > 0 ? request.Source : request.ProviderId)}");
        lock (_gate) _jobs.Add(job);
        _audit.Record(new AuditEvent("DownloadQueued", "DownloadManager", AuditResult.Success, request.Target, SecurityClassification.Low, Detail: $"{request.ProviderId}; {request.Kind}"));
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        _ = RunAsync(job);
        return OperationResult<DownloadJob>.Ok(job, "Queued.");
    }

    public void Cancel(string jobId)
    {
        var job = Find(jobId);
        if (job is null) return;
        if (job.Phase is DownloadPhase.Queued or DownloadPhase.Paused)
            _dispatch(() => { job.Phase = DownloadPhase.Cancelled; job.Status = "Cancelled."; job.FinishedAt = DateTimeOffset.UtcNow; });
        job.Cts?.Cancel();
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops an Ollama pull keeping the finished layers; <see cref="Resume"/> continues from them.</summary>
    public OperationResult Pause(string jobId)
    {
        var job = Find(jobId);
        if (job is null || !CanPause(job)) return OperationResult.Fail("This item cannot be paused (only resumable downloads can).");
        job.PauseRequested = true;
        job.Cts?.Cancel();
        return OperationResult.Ok("Pausing…");
    }

    public OperationResult Resume(string jobId)
    {
        var job = Find(jobId);
        if (job is null || !job.IsPaused) return OperationResult.Fail("Only a paused item can be resumed.");
        lock (_gate) _jobs.Remove(job);
        return Enqueue(job.Request) is { Success: true } ? OperationResult.Ok("Resumed (finished parts are kept).") : OperationResult.Fail("Could not resume.");
    }

    public OperationResult<DownloadJob> Retry(string jobId)
    {
        var job = Find(jobId);
        if (job is null || !job.IsFinished) return OperationResult<DownloadJob>.Fail("Only finished, failed or interrupted items can be retried.");
        if (job.Request.ApprovedPlanFingerprint is not null && job.Phase is not (DownloadPhase.Failed or DownloadPhase.Interrupted or DownloadPhase.Cancelled))
            return OperationResult<DownloadJob>.Fail("Installs are not repeated from the queue; plan the operation again from the catalogue.");
        lock (_gate) _jobs.Remove(job);
        return Enqueue(job.Request);
    }

    public OperationResult Remove(string jobId)
    {
        var job = Find(jobId);
        if (job is null || !job.IsFinished) return OperationResult.Fail("Only finished entries can be removed.");
        lock (_gate) _jobs.Remove(job);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return OperationResult.Ok("Removed from the list (nothing was uninstalled).");
    }

    public void ClearFinished()
    {
        lock (_gate) _jobs.RemoveAll(j => j.IsFinished);
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DownloadJob? Find(string id) { lock (_gate) return _jobs.FirstOrDefault(j => j.Id == id); }

    private async Task RunAsync(DownloadJob job)
    {
        await _worker.WaitAsync().ConfigureAwait(false);
        try
        {
            if (job.Phase is DownloadPhase.Cancelled) return;
            using var cts = new CancellationTokenSource();
            job.Cts = cts;
            var provider = _providers[job.Request.ProviderId];
            var sw = Stopwatch.StartNew();
            long lastBytes = 0;
            var lastTick = TimeSpan.Zero;
            string? lastLayer = null;

            // Progress<T> posts callbacks asynchronously: a late report must never overwrite the final state.
            var closed = 0;
            var progress = new Progress<DownloadProgress>(p => _dispatch(() =>
            {
                if (Volatile.Read(ref closed) == 1) return;
                job.Phase = p.Phase == DownloadPhase.Completed ? DownloadPhase.Verifying : p.Phase;
                job.Status = p.Status;
                if (p.BytesTotal is > 0)
                {
                    // A new layer resets the counters; restart the speed window.
                    var key = p.BytesTotal.ToString();
                    if (key != lastLayer) { lastLayer = key; lastBytes = p.BytesDone ?? 0; lastTick = sw.Elapsed; }
                    job.BytesTotal = p.BytesTotal;
                    job.BytesDone = p.BytesDone;
                    var dt = (sw.Elapsed - lastTick).TotalSeconds;
                    if (dt >= 1 && p.BytesDone is { } d)
                    {
                        job.BytesPerSecond = Math.Max(0, (d - lastBytes) / dt);
                        lastBytes = d;
                        lastTick = sw.Elapsed;
                    }
                }
            }));

            _dispatch(() => { job.Phase = DownloadPhase.Resolving; job.Status = "Resolving source and checking prerequisites"; });
            OperationResult result;
            try { result = await provider.ExecuteAsync(job.Request, progress, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { result = OperationResult.Fail("Cancelled.", "cancelled"); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result = OperationResult.Fail(ex.Message); }

            await Task.Delay(50).ConfigureAwait(false); // let queued progress callbacks land before the final state
            if (!result.Success)
            {
                Volatile.Write(ref closed, 1);
                _dispatch(() =>
                {
                    job.BytesPerSecond = 0;
                    if (job.PauseRequested) { job.Phase = DownloadPhase.Paused; job.Status = "Paused. Finished parts are kept; Resume continues from them."; job.PauseRequested = false; }
                    else if (cts.IsCancellationRequested || result.ErrorCode == "cancelled") { job.Phase = DownloadPhase.Cancelled; job.Status = "Cancelled."; job.FinishedAt = DateTimeOffset.UtcNow; }
                    else { job.Phase = DownloadPhase.Failed; job.Status = "Failed"; job.Error = result.Message; job.FinishedAt = DateTimeOffset.UtcNow; }
                });
                _log.Warn("Downloads", $"{job.Request.DisplayName}: {result.Message}");
                _audit.Record(new AuditEvent("DownloadFinished", "DownloadManager", AuditResult.Failure, job.Request.Target, SecurityClassification.Low, Detail: result.Message));
                return;
            }

            // Testing: nothing is called Ready because its bytes arrived.
            _dispatch(() => { job.BytesPerSecond = 0; job.Phase = DownloadPhase.Testing; job.Status = result.Message + " — testing…"; });
            var verifier = _verifiers.FirstOrDefault(v => v.Handles(job.Request));
            ItemVerification verification;
            if (verifier is null)
                verification = new ItemVerification(ItemVerdict.ReadyWithLimitations, "Downloaded and integrity-checked; no post-install test exists for this item type.", []);
            else
            {
                try { verification = await verifier.VerifyAsync(job.Request, progress, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { verification = new ItemVerification(ItemVerdict.ReadyWithLimitations, "Testing was cancelled; the item is installed but unverified.", []); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { verification = new ItemVerification(ItemVerdict.Failed, "Testing failed: " + ex.Message, []); }
            }

            var isInstall = job.Request.Kind is QueueItemKind.AgentTool or QueueItemKind.ModelRuntime or QueueItemKind.Gateway or QueueItemKind.Extension;
            Volatile.Write(ref closed, 1);
            _dispatch(() =>
            {
                foreach (var c in verification.Checks) job.AddLog("Check: " + c);
                job.VerificationResult = verification.Summary;
                job.FinalLocation = verification.FinalLocation;
                job.FinishedAt = DateTimeOffset.UtcNow;
                (job.Phase, job.Status) = verification.Verdict switch
                {
                    ItemVerdict.Ready => (DownloadPhase.Ready, verification.Label ?? "Ready"),
                    ItemVerdict.ReadyWithLimitations => (DownloadPhase.ReadyWithLimitations, verification.Label ?? "Ready with limitations"),
                    _ when isInstall => (DownloadPhase.RollbackRequired, "Installed, but the post-install test failed. Review, then roll back from the catalogue."),
                    _ => (DownloadPhase.Failed, verification.Label ?? "Failed verification"),
                };
                if (verification.Verdict == ItemVerdict.Failed) job.Error = verification.Summary;
            });
            _log.Info("Downloads", $"{job.Request.DisplayName}: {job.StageLabel} — {verification.Summary}");
            _audit.Record(new AuditEvent("DownloadFinished", "DownloadManager", verification.Verdict == ItemVerdict.Failed ? AuditResult.Failure : AuditResult.Success,
                job.Request.Target, SecurityClassification.Low, Detail: $"{job.StageLabel}: {verification.Summary}"));
        }
        finally
        {
            job.Cts = null;
            _worker.Release();
            Persist();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- persistence ------------------------------------------------------------------------------------------------

    public sealed record PersistedJob(string Id, DownloadRequest Request, DownloadPhase Phase, string Status, string? Error, string? Verification,
        string? FinalLocation, DateTimeOffset QueuedAt, DateTimeOffset? FinishedAt);

    public sealed class Doc
    {
        public int SchemaVersion { get; set; } = 1;
        public List<PersistedJob> Jobs { get; set; } = [];
    }

    private void Persist()
    {
        if (_store is null) return;
        List<PersistedJob> snapshot;
        lock (_gate)
            snapshot = _jobs.TakeLast(200).Select(j => new PersistedJob(j.Id, j.Request, j.Phase, j.Status, j.Error, j.VerificationResult, j.FinalLocation, j.QueuedAt, j.FinishedAt)).ToList();
        try { _store.Save(StoreName, new Doc { Jobs = snapshot }); }
        catch (IOException ex) { _log.Warn("Downloads", "Queue history could not be saved: " + ex.Message); }
    }

    private void Restore(IDocumentStore store)
    {
        foreach (var p in store.Load(StoreName, () => new Doc()).Jobs)
        {
            if (p.Request is null || string.IsNullOrWhiteSpace(p.Id)) continue;
            var job = new DownloadJob(p.Request, p.Id, p.QueuedAt);
            var wasRunning = p.Phase is not (DownloadPhase.Ready or DownloadPhase.ReadyWithLimitations or DownloadPhase.Failed or DownloadPhase.Cancelled
                or DownloadPhase.RollbackRequired or DownloadPhase.Interrupted);
            job.Phase = wasRunning ? DownloadPhase.Interrupted : p.Phase;
            job.Status = wasRunning ? "Interrupted: the app closed while this was in progress. Retry to continue (downloads resume from finished parts)." : p.Status;
            job.Error = p.Error;
            job.VerificationResult = p.Verification;
            job.FinalLocation = p.FinalLocation;
            job.FinishedAt = p.FinishedAt ?? (wasRunning ? DateTimeOffset.UtcNow : null);
            _jobs.Add(job);
        }
    }
}
