using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Recommendations;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Integrations.HuggingFace;
using DarkCipher.AgentControl.Integrations.Ollama;
using DarkCipher.AgentControl.Security.Policy;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed record InstalledRow(ModelDescriptor Model, string Verdict)
{
    public string Name => Model.Name;
    public string Params => Model.ParameterSize ?? "?";
    public string Quant => Model.Quantization ?? "?";
    public long? Size => Model.SizeBytes;
    public string Context => Model.ContextLength is { } c ? c.ToString("N0") : "?";
    public string Caps => string.Join(", ", Enum.GetValues<ModelCapabilities>().Where(c => c != ModelCapabilities.None && Model.Supports(c)));
}

/// <summary>
/// A model you can download (Ollama library row or recommendation), with its live queue state: the action button reads
/// Download → Queued → 45 % → Testing → Installed, or Resume / Retry when a download was paused, interrupted or failed
/// (Ollama keeps the finished layers, so a retry continues instead of starting again).
/// </summary>
public sealed class ModelOfferRow : ObservableObject
{
    private DownloadJob? _job;
    private bool _installed;

    public ModelOfferRow(CatalogModel model, bool installed, MemoryFit fit, Recommendation? rec = null)
    {
        Model = model;
        _installed = installed;
        Fit = fit;
        Rec = rec;
    }

    public CatalogModel Model { get; }
    public MemoryFit Fit { get; }
    public Recommendation? Rec { get; }
    public string Name => Model.OllamaName;
    public string Size => Bytes.Format(Model.ApproxSizeBytes) + (Model.SizeIsApproximate ? " approx." : "");
    public string Caps => string.Join(", ", Enum.GetValues<ModelCapabilities>().Where(c => c != ModelCapabilities.None && Model.Capabilities.HasFlag(c)));
    public string Context => Model.MaxContext.ToString("N0");
    public string License => $"{Model.LicenseName} ({Model.License})";
    public string Reason => Rec?.Reason ?? Model.Notes;
    public string CompatibleAgents => Rec is { CompatibleAgents.Count: > 0 } r ? string.Join(", ", r.CompatibleAgents) : "—";
    public IReadOnlyList<string> Limitations => Rec?.Limitations ?? [];

    public bool Installed { get => _installed; set { if (Set(ref _installed, value)) RaiseState(); } }

    public DownloadJob? Job
    {
        get => _job;
        set
        {
            if (ReferenceEquals(_job, value)) return;
            if (_job is not null) _job.PropertyChanged -= OnJobChanged;
            _job = value;
            if (_job is not null) _job.PropertyChanged += OnJobChanged;
            RaiseState();
        }
    }

    private void OnJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadJob.Percent) or nameof(DownloadJob.Phase) or nameof(DownloadJob.StageLabel) or nameof(DownloadJob.Status))
        {
            if (_job is { Phase: DownloadPhase.Ready or DownloadPhase.ReadyWithLimitations }) _installed = true;
            RaiseState();
        }
    }

    private void RaiseState() { Raise(nameof(ActionLabel)); Raise(nameof(CanAct)); Raise(nameof(StateText)); Raise(nameof(Installed)); }

    public string ActionLabel => Installed ? "Installed" : Job switch
    {
        { IsPaused: true } => "Resume",
        { Phase: DownloadPhase.Cancelled } => "Download",
        { IsFailed: true } => "Retry",
        { Phase: DownloadPhase.Queued } => "Queued",
        { IsActive: true } j => j.Percent is { } p && j.Phase == DownloadPhase.Downloading ? $"{p:0}%" : j.StageLabel,
        _ => "Download",
    };

    public bool CanAct => !Installed && (Job is null || Job.IsPaused || Job.IsFailed || Job.Phase == DownloadPhase.Cancelled);

    /// <summary>One line for the row: what happened to the last download of this tag.</summary>
    public string StateText => Installed ? "Installed in Ollama" : Job switch
    {
        null => "Not downloaded",
        { IsPaused: true } => "Paused; finished parts are kept",
        { IsFailed: true } j => "Stopped: " + (j.Error ?? j.Status),
        { Phase: DownloadPhase.Cancelled } => "Cancelled",
        { } j => $"{j.StageLabel}: {j.Status}",
    };
}

public sealed class ModelsViewModel : PageViewModel, IContextActionProvider
{
    private readonly OllamaRuntime _ollama;
    private readonly RecommendationEngine _engine;
    private readonly HardwareService _hardware;
    private readonly DownloadManager _downloads;
    private readonly OllamaCompatibilitySuite _suite;
    private readonly GatewayProbe _gateway;
    private readonly CompatibilityStore _compat;
    private readonly HuggingFaceSource _hf;
    private readonly SecurityPolicyService _policy;
    private readonly IProcessExecutor _processes;
    private readonly Navigator _nav;
    private InstalledRow? _selectedInstalled;
    private ModelOfferRow? _selectedCatalog;
    private RecommendationProfile _profile = RecommendationProfile.AgenticCoding;
    private string _catalogFilter = "";
    private bool _codingOnly, _toolsOnly, _fitsOnly = true;
    private string _hfQuery = "coder gguf";
    private bool _ggufOnly = true;
    private ModelSourceEntry? _selectedHf;
    private string _hfDetails = "";
    private HfImportPlan? _importPlan;
    private string _pullName = "";
    private string _pendingSignature = "";

    public ModelsViewModel(OllamaRuntime ollama, RecommendationEngine engine, HardwareService hardware, DownloadManager downloads, OllamaCompatibilitySuite suite,
        GatewayProbe gateway, CompatibilityStore compat, HuggingFaceSource hf, SecurityPolicyService policy, IProcessExecutor processes, Navigator nav)
    {
        _ollama = ollama; _engine = engine; _hardware = hardware; _downloads = downloads; _suite = suite; _gateway = gateway; _compat = compat; _hf = hf; _policy = policy;
        _processes = processes; _nav = nav;
        RefreshCommand = new AsyncCommand(() => Guard(LoadAsync, "Refresh failed"));
        TestCommand = new AsyncCommand(p => Guard(() => TestAsync(Row(p)), "Compatibility test failed"), p => Row(p) is not null);
        GatewayTestCommand = new AsyncCommand(p => Guard(() => GatewayTestAsync(Row(p)), "Gateway test failed"), p => Row(p) is not null);
        UnloadCommand = new AsyncCommand(p => Guard(() => UnloadAsync(Row(p)), "Unload failed"), p => Row(p) is not null);
        RemoveCommand = new AsyncCommand(p => Guard(() => RemoveAsync(Row(p)), "Remove failed"), p => Row(p) is not null);
        CopyNameCommand = new RelayCommand(p => { if (Row(p) is { } r) Ui.Copy(r.Name); }, p => Row(p) is not null);
        DetailsCommand = new AsyncCommand(p => Guard(() => DetailsAsync(Row(p)), "Details failed"), p => Row(p) is not null);
        UseInTemplateCommand = new RelayCommand(() => nav.Go("templates"));
        InstallCommand = new RelayCommand(p => Offer(p as ModelOfferRow ?? SelectedCatalog, VerifyAfterDownload), p => (p as ModelOfferRow ?? SelectedCatalog) is { CanAct: true });
        RowActionCommand = new RelayCommand(p => Offer(p as ModelOfferRow, VerifyAfterDownload), p => p is ModelOfferRow { CanAct: true });
        DownloadNoTestCommand = new RelayCommand(p => Offer(p as ModelOfferRow, verify: false), p => p is ModelOfferRow { CanAct: true, Job: null });
        CopyOfferNameCommand = new RelayCommand(p => { if (p is ModelOfferRow r) Ui.Copy(r.Name); });
        OpenLibraryPageCommand = new AsyncCommand(p => p is ModelOfferRow r ? OpenUrlAsync("https://ollama.com/library/" + r.Name.Split(':')[0], "Open the Ollama library page") : Task.CompletedTask);
        PullByNameCommand = new RelayCommand(PullByName, () => ModelNameRules.IsValid(PullName.Trim()));
        ResumeJobCommand = new RelayCommand(p => { if (p is DownloadJob j) Message = _downloads.Resume(j.Id).Message; }, p => p is DownloadJob { IsPaused: true });
        RetryJobCommand = new RelayCommand(p => { if (p is DownloadJob j) Message = _downloads.Retry(j.Id).Message; }, p => p is DownloadJob { IsFailed: true } or DownloadJob { Phase: DownloadPhase.Cancelled });
        PauseJobCommand = new RelayCommand(p => { if (p is DownloadJob j) Message = _downloads.Pause(j.Id).Message; }, p => p is DownloadJob j && _downloads.CanPause(j));
        CancelJobCommand = new RelayCommand(p => { if (p is DownloadJob j) _downloads.Cancel(j.Id); }, p => p is DownloadJob { IsFinished: false });
        RemoveJobCommand = new RelayCommand(p => { if (p is DownloadJob j) Message = _downloads.Remove(j.Id).Message; }, p => p is DownloadJob { IsFinished: true });
        RecommendCommand = new AsyncCommand(() => Guard(RecommendAsync, "Recommendation failed"));
        HfSearchCommand = new AsyncCommand(() => Guard(HfSearchAsync, "Hugging Face search failed"));
        HfImportCommand = new RelayCommand(HfImport, () => _importPlan is { OllamaReference: not null, Feasibility: not ImportFeasibility.Unsupported });
        HfOpenCommand = new AsyncCommand(p => p is ModelSourceEntry e ? OpenUrlAsync("https://huggingface.co/" + e.Id, "Open the Hugging Face repository page") : Task.CompletedTask);
        HfCopyIdCommand = new RelayCommand(p => { if (p is ModelSourceEntry e) Ui.Copy(e.Id); });
        OpenDownloadsCommand = new RelayCommand(() => nav.Go("downloads"));
        downloads.Changed += (_, _) => Ui.OnUi(SyncQueue);
    }

    public override string Title => "Models";
    public override string Subtitle => "Installed models, recommendations for this PC, model sources and real compatibility results. Every model has a Download button; right-click any row for more.";

    public ObservableCollection<InstalledRow> Installed { get; } = [];
    public ObservableCollection<ModelOfferRow> Recommendations { get; } = [];
    public ObservableCollection<ModelOfferRow> Catalog { get; } = [];
    public ObservableCollection<ModelSourceEntry> HfResults { get; } = [];
    public ObservableCollection<AgentMatrixEntry> Matrix { get; } = [];
    public ObservableCollection<CompatibilityTestResult> LastResults { get; } = [];
    /// <summary>Model downloads that are queued, running, paused, interrupted or failed — the ones that still need you.</summary>
    public ObservableCollection<DownloadJob> Pending { get; } = [];
    public bool HasPending => Pending.Count > 0;

    public InstalledRow? SelectedInstalled { get => _selectedInstalled; set => Set(ref _selectedInstalled, value); }
    public ModelOfferRow? SelectedCatalog { get => _selectedCatalog; set => Set(ref _selectedCatalog, value); }
    public IReadOnlyList<RecommendationProfile> Profiles { get; } = Enum.GetValues<RecommendationProfile>();
    public RecommendationProfile Profile { get => _profile; set { if (Set(ref _profile, value)) _ = Guard(RecommendAsync, "Recommendation failed"); } }
    public string CatalogFilter { get => _catalogFilter; set { if (Set(ref _catalogFilter, value)) _ = Guard(BuildCatalogAsync, "Filter failed"); } }
    public bool CodingOnly { get => _codingOnly; set { if (Set(ref _codingOnly, value)) _ = Guard(BuildCatalogAsync, "Filter failed"); } }
    public bool ToolsOnly { get => _toolsOnly; set { if (Set(ref _toolsOnly, value)) _ = Guard(BuildCatalogAsync, "Filter failed"); } }
    public bool FitsOnly { get => _fitsOnly; set { if (Set(ref _fitsOnly, value)) _ = Guard(BuildCatalogAsync, "Filter failed"); } }
    public string HfQuery { get => _hfQuery; set => Set(ref _hfQuery, value); }
    public bool GgufOnly { get => _ggufOnly; set => Set(ref _ggufOnly, value); }
    public string PullName { get => _pullName; set => Set(ref _pullName, value); }
    public ModelSourceEntry? SelectedHf { get => _selectedHf; set { if (Set(ref _selectedHf, value) && value is not null) _ = Guard(HfDetailsAsync, "Hugging Face details failed"); } }
    public string HfDetails { get => _hfDetails; private set => Set(ref _hfDetails, value); }
    public string HfEndpointNote => _policy.IsEndpointApproved(HuggingFaceSource.ApiBase)
        ? "huggingface.co is approved for model catalogue requests."
        : "huggingface.co is not approved yet. The first search asks you to approve it (metadata only; no code is downloaded or run).";

    public ICommand RefreshCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand GatewayTestCommand { get; }
    public ICommand UnloadCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand CopyNameCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand UseInTemplateCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand RowActionCommand { get; }
    public ICommand DownloadNoTestCommand { get; }
    public ICommand CopyOfferNameCommand { get; }
    public ICommand OpenLibraryPageCommand { get; }
    public ICommand PullByNameCommand { get; }
    public ICommand ResumeJobCommand { get; }
    public ICommand RetryJobCommand { get; }
    public ICommand PauseJobCommand { get; }
    public ICommand CancelJobCommand { get; }
    public ICommand RemoveJobCommand { get; }
    public ICommand RecommendCommand { get; }
    public ICommand HfSearchCommand { get; }
    public ICommand HfImportCommand { get; }
    public ICommand HfOpenCommand { get; }
    public ICommand HfCopyIdCommand { get; }
    public ICommand OpenDownloadsCommand { get; }

    /// <summary>Commands act on the row passed as the parameter (right-click) or, for toolbar buttons, the selected row.</summary>
    private InstalledRow? Row(object? p) => p as InstalledRow ?? SelectedInstalled;

    protected override Task OnActivateAsync() => LoadAsync();

    private IReadOnlyList<ModelDescriptor> _installedCache = [];

    private async Task LoadAsync()
    {
        Installed.Clear();
        try { _installedCache = await _ollama.ListModelsAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or NetworkBlockedException)
        {
            _installedCache = [];
            Message = "Ollama is not reachable: " + ex.Message;
        }
        foreach (var m in _installedCache)
        {
            var r = _compat.Latest(m.Name, m.Digest);
            Installed.Add(new InstalledRow(m, r is null ? "Not tested" : Verdict(r.Verdict)));
        }
        Matrix.Clear();
        foreach (var row in _compat.Matrix(_installedCache)) Matrix.Add(row);
        await BuildCatalogAsync();
        await RecommendAsync();
        SyncQueue();
    }

    public static string Verdict(CompatibilityVerdict v) => v switch
    {
        CompatibilityVerdict.AgenticCompatible => "Agentic compatible",
        CompatibilityVerdict.TextGenerationOnly => "Text generation only (agentic failed)",
        CompatibilityVerdict.Failed => "Failed",
        _ => "Not tested",
    };

    private bool IsInstalled(string ollamaName) => _installedCache.Any(i => string.Equals(i.Name, ollamaName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(i.Name, ollamaName + ":latest", StringComparison.OrdinalIgnoreCase));

    private async Task BuildCatalogAsync()
    {
        var hw = await _hardware.GetAsync(CancellationToken.None);
        var budget = RecommendationEngine.Budget(hw);
        Catalog.Clear();
        foreach (var m in ModelCatalog.Curated)
        {
            var fit = RecommendationEngine.Fit(RecommendationEngine.Estimate(m, 16384), budget);
            if (CodingOnly && !m.CodingSpecialised) continue;
            if (ToolsOnly && !m.Capabilities.HasFlag(ModelCapabilities.Tools)) continue;
            if (FitsOnly && fit is MemoryFit.DoesNotFit or MemoryFit.PartialGpuOffload) continue;
            if (CatalogFilter.Length > 0 && !m.OllamaName.Contains(CatalogFilter, StringComparison.OrdinalIgnoreCase) && !m.DisplayName.Contains(CatalogFilter, StringComparison.OrdinalIgnoreCase)) continue;
            Catalog.Add(new ModelOfferRow(m, IsInstalled(m.OllamaName), fit) { Job = LatestJob(m.OllamaName) });
        }
    }

    private async Task RecommendAsync()
    {
        var hw = await _hardware.GetAsync(CancellationToken.None);
        IReadOnlyList<RunningModel> running = [];
        try { running = await _ollama.ListRunningAsync(CancellationToken.None); } catch (Exception ex) when (ex is HttpRequestException or TimeoutException or NetworkBlockedException) { }
        Recommendations.Clear();
        foreach (var r in _engine.Recommend(hw, Profile, _installedCache, 6, running))
            Recommendations.Add(new ModelOfferRow(r.Model, r.Installed, r.Fit, r) { Job = LatestJob(r.Model.OllamaName) });
        if (Profile == RecommendationProfile.CloudAssisted) Message = "Cloud-Assisted uses a vendor-hosted model through a native agent (Stacks → Claude Code Native or Codex native); no local model is recommended.";
    }

    private DownloadJob? LatestJob(string tag) => _downloads.Jobs
        .Where(j => j.Request.ProviderId == "ollama" && string.Equals(j.Request.Target, tag, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(j => j.QueuedAt).FirstOrDefault();

    /// <summary>Keeps every row's button in step with the queue, refreshes the unfinished list, and re-lists models once
    /// a download becomes Ready.</summary>
    private void SyncQueue()
    {
        foreach (var row in Catalog.Concat(Recommendations)) row.Job = LatestJob(row.Name);
        var pending = _downloads.Jobs
            .Where(j => j.Request.Kind is QueueItemKind.ModelPull or QueueItemKind.HuggingFaceImport && (!j.IsFinished || j.IsFailed || j.IsPaused))
            .OrderByDescending(j => j.QueuedAt).ToList();
        var signature = string.Join("|", pending.Select(j => j.Id + ":" + j.Phase));
        var readyNow = _downloads.Jobs.Count(j => j.Request.ProviderId == "ollama" && j.Phase is DownloadPhase.Ready or DownloadPhase.ReadyWithLimitations);
        if (signature != _pendingSignature)
        {
            _pendingSignature = signature;
            Pending.Clear();
            foreach (var j in pending) Pending.Add(j);
            Raise(nameof(HasPending));
        }
        if (readyNow != _readyCount)
        {
            var first = _readyCount < 0;
            _readyCount = readyNow;
            if (!first) _ = Guard(LoadAsync, "Refresh failed");
        }
    }

    private int _readyCount = -1;

    private async Task TestAsync(InstalledRow? row)
    {
        if (row is null) return;
        var m = row.Name;
        var hw = await _hardware.GetAsync(CancellationToken.None);
        Message = $"Testing {m}… (fixed prompts, on this PC only)";
        LastResults.Clear();
        var report = await _suite.RunAsync(m, HardwareAssessor.Summary(hw), 32768, new Progress<string>(t => Message = $"Testing {m}: {t}"), CancellationToken.None);
        _compat.Add(report);
        foreach (var r in report.Results) LastResults.Add(r);
        Message = $"{m}: {Verdict(report.Verdict)}" + (report.MeasuredTokensPerSecond is { } tps ? $" · measured {tps:0.0} tok/s in this run" : "");
        await LoadAsync();
    }

    private async Task GatewayTestAsync(InstalledRow? row)
    {
        if (row is null) return;
        var m = row.Name;
        Message = $"Testing Claude Code gateway with {m}…";
        var gw = await _gateway.ProbeAnthropicAsync(m, CancellationToken.None);
        _compat.AddGateway(new CompatibilityStore.GatewayResult("claude-code", AgentProtocol.AnthropicMessages, m, row.Model.Digest, gw.Compatibility, DateTimeOffset.UtcNow, $"Ollama {gw.Version}: {gw.Compatibility}"));
        var sb = new StringBuilder();
        sb.AppendLine($"Gateway: {gw.Name}  version {gw.Version}  ({gw.Source}, licence {gw.Licence})");
        sb.AppendLine($"Address: {gw.Address}   Protocol: {gw.Protocol}   Auth: {gw.Authentication}");
        sb.AppendLine($"Model: {gw.Model} (NOT Claude)   Health: {gw.Health}   Result: {gw.Compatibility}");
        sb.AppendLine();
        foreach (var l in gw.Limitations) sb.AppendLine("• " + l);
        Dialogs.ShowText("Claude Code local gateway", sb.ToString());
        Message = $"Gateway with {m}: {Verdict(gw.Compatibility)}";
        await LoadAsync();
    }

    private async Task UnloadAsync(InstalledRow? row)
    {
        if (row is null) return;
        Message = (await _ollama.UnloadAsync(row.Name, CancellationToken.None)).Message;
    }

    private async Task RemoveAsync(InstalledRow? m)
    {
        if (m is null) return;
        if (!Dialogs.Confirm("Remove model", $"Remove {m.Name} ({Bytes.Format(m.Size)}) from Ollama? Stacks using it will show 'Model missing'.", null, "Remove", danger: true)) return;
        var r = await _ollama.DeleteAsync(m.Name, CancellationToken.None);
        Message = r.Message;
        await LoadAsync();
    }

    private async Task DetailsAsync(InstalledRow? row)
    {
        if (row is null) return;
        var d = await _ollama.ShowAsync(row.Name, CancellationToken.None);
        var r = _compat.Latest(row.Name, row.Model.Digest);
        var sb = new StringBuilder();
        sb.AppendLine($"Name:            {row.Name}");
        sb.AppendLine($"Digest:          {row.Model.Digest}");
        sb.AppendLine($"Family:          {d?.Family}");
        sb.AppendLine($"Parameters:      {d?.ParameterSize}");
        sb.AppendLine($"Quantization:    {d?.Quantization}");
        sb.AppendLine($"Context:         {d?.ContextLength:N0}");
        sb.AppendLine($"Capabilities:    {row.Caps}");
        sb.AppendLine($"Licence (first line of the text Ollama reports): {d?.LicenseName ?? "not reported"}");
        sb.AppendLine($"Compatibility:   {(r is null ? "not tested" : $"{Verdict(r.Verdict)} on {r.RunAt:yyyy-MM-dd} (suite {r.SuiteVersion}, Ollama {r.RuntimeVersion})")}");
        if (r is not null) foreach (var t in r.Results) sb.AppendLine($"   {(t.Passed ? "PASS" : "FAIL")}  {t.Test}: {t.Detail}");
        Dialogs.ShowText(row.Name, sb.ToString());
    }

    private bool _verifyAfterDownload = true;
    /// <summary>Run the compatibility suite as the Testing stage (Ready only if it passes).</summary>
    public bool VerifyAfterDownload { get => _verifyAfterDownload; set => Set(ref _verifyAfterDownload, value); }

    /// <summary>The row's main button: resume a paused download, retry a stopped one, or plan and confirm a new one.</summary>
    private void Offer(ModelOfferRow? row, bool verify)
    {
        if (row is null || !row.CanAct) return;
        if (row.Job is { IsPaused: true } paused) { Message = _downloads.Resume(paused.Id).Message; return; }
        if (row.Job is { IsFailed: true } failed) { Message = _downloads.Retry(failed.Id).Message; return; }
        Download(row.Model.OllamaName, row.Model, verify);
    }

    private void Download(string tag, CatalogModel? curated, bool verify)
    {
        var plan = Application.Models.ModelInstallPlanner.ForOllamaTag(tag, curated, _hardware.Current, Composition.Bootstrapper.ModelDriveFreeBytes());
        if (!plan.CanProceed) { Dialogs.Show("Cannot download", "This model cannot be installed here.", plan.ApprovalDetails(), ["Close"], "Bad"); return; }
        var name = curated?.DisplayName ?? tag;
        if (!Dialogs.Confirm("Download model", $"Download {name}? Nothing has been downloaded yet.",
            [.. plan.ApprovalDetails(), verify ? "After download: compatibility suite (inference, JSON, tool calls) decides Ready." : "After download: visibility check only (Ready with limitations)."], "Download")) return;
        var r = _downloads.Enqueue(new DownloadRequest("ollama", tag, name, curated?.ApproxSizeBytes)
        {
            Kind = QueueItemKind.ModelPull, Source = "Ollama library", VersionOrTag = tag, VerifyForAgents = verify,
        });
        Message = r.Success ? $"{name} queued. The button shows its progress; full details are in Downloads." : r.Message;
        SyncQueue();
    }

    /// <summary>Download any Ollama library tag by name (for example qwen2.5-coder:7b), with the same plan and checks.</summary>
    private void PullByName()
    {
        var tag = PullName.Trim();
        if (!ModelNameRules.IsValid(tag)) { Message = "That is not a valid Ollama model name (for example qwen2.5-coder:7b)."; return; }
        if (IsInstalled(tag)) { Message = $"{tag} is already installed."; return; }
        var curated = ModelCatalog.Curated.FirstOrDefault(m => string.Equals(m.OllamaName, tag, StringComparison.OrdinalIgnoreCase));
        Download(tag, curated, VerifyAfterDownload);
        PullName = "";
    }

    private async Task OpenUrlAsync(string url, string purpose)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var r = await _processes.RunAsync(new ProcessSpec { Executable = explorer, Arguments = [u.AbsoluteUri], Timeout = TimeSpan.FromSeconds(10), Purpose = purpose }, CancellationToken.None);
        if (r.Outcome == ProcessOutcome.Blocked) Message = r.Message;
    }

    public IReadOnlyList<MenuAction> ContextActions(object item) => item switch
    {
        InstalledRow r =>
        [
            new("Test compatibility", TestCommand, r, Glyph: ""),
            new("Test Claude Code gateway", GatewayTestCommand, r, Glyph: ""),
            new("Details", DetailsCommand, r, Glyph: ""),
            new("Unload from memory", UnloadCommand, r, Glyph: ""),
            new("Use in a template", UseInTemplateCommand, Group: "Use", Glyph: ""),
            new("Copy name", CopyNameCommand, r, Group: "Copy", Glyph: ""),
            new("Remove…", RemoveCommand, r, Danger: true, Glyph: ""),
        ],
        ModelOfferRow o =>
        [
            new(o.ActionLabel is "Resume" or "Retry" ? o.ActionLabel : "Download…", RowActionCommand, o, Glyph: ""),
            new("Download without the agent test", DownloadNoTestCommand, o, Glyph: ""),
            new("Open in Downloads", OpenDownloadsCommand, Group: "Open", Glyph: ""),
            new("Open the Ollama library page", OpenLibraryPageCommand, o, Group: "Open", Glyph: ""),
            new("Copy name", CopyOfferNameCommand, o, Group: "Copy", Glyph: ""),
        ],
        DownloadJob j =>
        [
            new("Resume", ResumeJobCommand, j, Glyph: ""),
            new("Retry", RetryJobCommand, j, Glyph: ""),
            new("Pause", PauseJobCommand, j, Glyph: ""),
            new("Open in Downloads", OpenDownloadsCommand, Group: "Open", Glyph: ""),
            new("Remove from the list", RemoveJobCommand, j, Group: "Open", Glyph: ""),
            new("Cancel", CancelJobCommand, j, Danger: true, Glyph: ""),
        ],
        ModelSourceEntry e =>
        [
            new("Import through Ollama…", HfImportCommand, Glyph: ""),
            new("Open on huggingface.co", HfOpenCommand, e, Group: "Open", Glyph: ""),
            new("Copy repository id", HfCopyIdCommand, e, Group: "Copy", Glyph: ""),
        ],
        _ => [],
    };

    private async Task HfSearchAsync()
    {
        if (!_policy.IsEndpointApproved(HuggingFaceSource.ApiBase))
        {
            if (!Dialogs.Confirm("Approve Hugging Face", "Allow this app to contact https://huggingface.co for model search and metadata?",
                    ["Only the official Hub API is used.", "Search text is sent to Hugging Face.", "No code, scripts or installers from repositories are ever run.", "You can revoke this in Security → Network."], "Approve"))
                return;
            _policy.ApproveEndpoint(HuggingFaceSource.ApiBase, "Hugging Face model search (user approved)");
            Raise(nameof(HfEndpointNote));
        }
        HfResults.Clear();
        foreach (var e in await _hf.SearchAsync(new ModelSearchQuery(HfQuery, GgufOnly, Limit: 40), CancellationToken.None)) HfResults.Add(e);
        Message = $"{HfResults.Count} repositories found.";
    }

    private async Task HfDetailsAsync()
    {
        var d = await _hf.GetDetailsAsync(SelectedHf!.Id, CancellationToken.None);
        if (d is null) { HfDetails = "Repository not found."; return; }
        var hw = await _hardware.GetAsync(CancellationToken.None);
        _importPlan = HfImportPlanner.Plan(d, hw, Composition.Bootstrapper.ModelDriveFreeBytes() ?? 0);
        _hfDetailsCache = d;
        var sb = new StringBuilder();
        sb.AppendLine(d.Attribution);
        sb.AppendLine($"Licence class: {d.Entry.License}   Gated: {d.Entry.Gated}   Private: {d.Entry.Private}");
        sb.AppendLine($"GGUF files: {d.Files.Count(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))}");
        foreach (var f in d.Files.Where(f => f.Quantization is not null).OrderBy(f => f.SizeBytes).Take(12)) sb.AppendLine($"   {f.Quantization,-8} {Bytes.Format(f.SizeBytes),10}  {f.Path}");
        sb.AppendLine();
        sb.AppendLine($"Import: {_importPlan.Feasibility}" + (_importPlan.OllamaReference is null ? "" : $" → {_importPlan.OllamaReference} ({Bytes.Format(_importPlan.SizeBytes)})"));
        foreach (var s in _importPlan.Steps) sb.AppendLine("  • " + s);
        foreach (var w in _importPlan.Warnings) sb.AppendLine("  ⚠ " + w);
        HfDetails = sb.ToString();
        CommandManager.InvalidateRequerySuggested();
    }

    private ModelSourceDetails? _hfDetailsCache;

    private void HfImport()
    {
        var p = _importPlan!;
        var plan = _hfDetailsCache is null ? null : Application.Models.ModelInstallPlanner.ForHuggingFace(p, _hfDetailsCache, _hardware.Current);
        if (plan is { CanProceed: false }) { Dialogs.Show("Cannot import", "This repository has no supported acquisition path here.", plan.ApprovalDetails(), ["Close"], "Bad"); return; }
        if (!Dialogs.Confirm("Import from Hugging Face", $"Import {p.RepoId} ({p.Quantization}) through Ollama? Nothing has been downloaded yet.",
            [.. plan?.ApprovalDetails() ?? p.Steps, p.Attribution], "Import")) return;
        var r = _downloads.Enqueue(new DownloadRequest("ollama", p.OllamaReference!, $"{p.RepoId} {p.Quantization}", p.SizeBytes)
        {
            Kind = QueueItemKind.HuggingFaceImport, Source = "huggingface.co/" + p.RepoId, VersionOrTag = p.Quantization, VerifyForAgents = VerifyAfterDownload,
        });
        Message = r.Success ? "Import queued — see Downloads. It becomes Ready only if the tests pass." : r.Message;
    }
}
