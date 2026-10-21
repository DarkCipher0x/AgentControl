using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Appearance;
using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Sessions;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Application.Verification;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Sessions;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Domain.Verification;
using DarkCipher.AgentControl.Integrations.Ollama;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed record StatusRow(string Name, string Status, string Detail, Brush Brush);

/// <summary>One card on the dashboard (order, size and visibility come from the saved layout).</summary>
public sealed class DashboardCardViewModel(DashboardCatalog.CardInfo info, bool wide, DashboardViewModel owner) : ObservableObject
{
    public string Id => info.Id;
    public string Title => info.Title;
    public bool IsWide { get; } = wide;
    public bool CanBeWide => info.CanBeWide;
    public DashboardViewModel Owner => owner;
}

public sealed record QuickActionViewModel(string Id, string Title, ICommand Command, bool Primary);

public sealed class DashboardViewModel : PageViewModel
{
    private readonly HardwareService _hardware;
    private readonly OllamaRuntime _ollama;
    private readonly IEnumerable<IAgentProvider> _agents;
    private readonly StackService _stacks;
    private readonly IStackValidator _validator;
    private readonly SessionService _sessions;
    private readonly IAuditLogger _audit;
    private readonly AppearanceSettingsService _appearance;
    private readonly DownloadManager _downloads;
    private readonly SupportedConfigurationMatrix _matrix;
    private readonly Navigator _nav;
    private readonly List<StatusRow> _allStacks = [];
    private string _hardwareHeadline = "Scanning…";
    private string _hardwareDetail = "";
    private string _runtimeSummary = "Checking…";
    private Brush _runtimeBrush = StatusBrushes.Muted;
    private string _sessionSummary = "";
    private string _securitySummary = "";
    private bool _customising;

    public DashboardViewModel(HardwareService hardware, OllamaRuntime ollama, IEnumerable<IAgentProvider> agents, StackService stacks,
        IStackValidator validator, SessionService sessions, IAuditLogger audit, Navigator nav, AppearanceSettingsService appearance,
        DownloadManager downloads, SupportedConfigurationMatrix matrix)
    {
        _hardware = hardware;
        _ollama = ollama;
        _agents = agents;
        _stacks = stacks;
        _validator = validator;
        _sessions = sessions;
        _audit = audit;
        _appearance = appearance;
        _downloads = downloads;
        _matrix = matrix;
        _nav = nav;
        RefreshCommand = new AsyncCommand(() => Guard(RefreshAsync, "Refresh failed"));
        GoSessionsCommand = new RelayCommand(() => nav.Go("sessions"));
        GoStacksCommand = new RelayCommand(() => nav.Go("stacks"));
        GoDownloadsCommand = new RelayCommand(() => nav.Go("downloads"));
        ToggleCustomiseCommand = new RelayCommand(() => Customising = !Customising);
        MoveUpCommand = new RelayCommand(p => Move(p as string, -1));
        MoveDownCommand = new RelayCommand(p => Move(p as string, +1));
        ToggleWideCommand = new RelayCommand(p => EditCard(p as string, c => c with { Size = c.Size == CardSize.Wide ? CardSize.Normal : CardSize.Wide }));
        HideCommand = new RelayCommand(p => EditCard(p as string, c => c with { Visible = false }));
        ShowCommand = new RelayCommand(p => EditCard(p as string, c => c with { Visible = true }));
        ResetLayoutCommand = new RelayCommand(() => Message = _appearance.Reset(AppearanceCategory.Dashboard).Message);
        ToggleModeCommand = new RelayCommand(() => _appearance.Update(s => s with { Dashboard = s.Dashboard with { Mode = s.Dashboard.Mode == DashboardMode.Detailed ? DashboardMode.Compact : DashboardMode.Detailed } }));
        ToggleAttentionFilterCommand = new RelayCommand(() => _appearance.Update(s => s with { Dashboard = s.Dashboard with { OnlyStacksNeedingAttention = !s.Dashboard.OnlyStacksNeedingAttention } }));
        appearance.Changed += (_, _) => Ui.OnUi(BuildLayout);
        downloads.Changed += (_, _) => Ui.OnUi(RefreshDownloads);
        BuildLayout();
    }

    public override string Title => "Dashboard";
    public override string Subtitle => "Readiness of your agentic coding stacks, with the reason behind every status.";

    public string HardwareHeadline { get => _hardwareHeadline; private set => Set(ref _hardwareHeadline, value); }
    public string HardwareDetail { get => _hardwareDetail; private set => Set(ref _hardwareDetail, value); }
    public string RuntimeSummary { get => _runtimeSummary; private set => Set(ref _runtimeSummary, value); }
    public Brush RuntimeBrush { get => _runtimeBrush; private set => Set(ref _runtimeBrush, value); }
    public string SessionSummary { get => _sessionSummary; private set => Set(ref _sessionSummary, value); }
    public string SecuritySummary { get => _securitySummary; private set => Set(ref _securitySummary, value); }
    public ObservableCollection<StatusRow> Agents { get; } = [];
    public ObservableCollection<StatusRow> Stacks { get; } = [];
    public ObservableCollection<StatusRow> LoadedModels { get; } = [];
    public ObservableCollection<StatusRow> DownloadRows { get; } = [];
    public ObservableCollection<StatusRow> VerifiedRows { get; } = [];

    public ObservableCollection<DashboardCardViewModel> Cards { get; } = [];
    public ObservableCollection<DashboardCardViewModel> HiddenCards { get; } = [];
    public ObservableCollection<QuickActionViewModel> QuickActions { get; } = [];
    public int Columns => _appearance.Current.Dashboard.Mode == DashboardMode.Compact ? 4 : 3;
    public bool ShowHardwareDetails => _appearance.Current.Dashboard.ShowHardwareDetails && _appearance.Current.Dashboard.Mode == DashboardMode.Detailed;
    public bool OnlyNeedingAttention => _appearance.Current.Dashboard.OnlyStacksNeedingAttention;
    public string ModeLabel => _appearance.Current.Dashboard.Mode == DashboardMode.Detailed ? "Compact layout" : "Detailed layout";
    public string FilterLabel => OnlyNeedingAttention ? "Show all stacks" : "Only stacks needing attention";
    public bool Customising { get => _customising; set => Set(ref _customising, value); }

    public ICommand RefreshCommand { get; }
    public ICommand GoSessionsCommand { get; }
    public ICommand GoStacksCommand { get; }
    public ICommand GoDownloadsCommand { get; }
    public ICommand ToggleCustomiseCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ToggleWideCommand { get; }
    public ICommand HideCommand { get; }
    public ICommand ShowCommand { get; }
    public ICommand ResetLayoutCommand { get; }
    public ICommand ToggleModeCommand { get; }
    public ICommand ToggleAttentionFilterCommand { get; }

    protected override Task OnActivateAsync() => RefreshAsync();

    private void BuildLayout()
    {
        var layout = _appearance.Current.Dashboard;
        var info = DashboardCatalog.Cards.ToDictionary(c => c.Id);
        Cards.Clear();
        HiddenCards.Clear();
        foreach (var c in layout.Cards)
        {
            var vm = new DashboardCardViewModel(info[c.Id], c.Size == CardSize.Wide, this);
            (c.Visible ? Cards : HiddenCards).Add(vm);
        }
        QuickActions.Clear();
        var first = true;
        foreach (var id in layout.QuickActions)
        {
            var title = DashboardCatalog.QuickActions.First(a => a.Id == id).Title;
            QuickActions.Add(new QuickActionViewModel(id, title, QuickActionCommand(id), first));
            first = false;
        }
        Raise(nameof(Columns));
        Raise(nameof(ShowHardwareDetails));
        Raise(nameof(OnlyNeedingAttention));
        Raise(nameof(ModeLabel));
        Raise(nameof(FilterLabel));
        ApplyStackFilter();
    }

    private ICommand QuickActionCommand(string id) => id switch
    {
        "verify" => new AsyncCommand(() => Guard(RefreshAsync, "Verification failed")),
        "setup" or "catalogue" or "templates" or "sessions" or "downloads" => new RelayCommand(() => _nav.Go(id)),
        _ => new RelayCommand(() => { }),
    };

    private void Move(string? id, int delta)
    {
        if (id is null) return;
        _appearance.Update(s =>
        {
            var cards = s.Dashboard.Cards.ToList();
            var visible = cards.Where(c => c.Visible).ToList();
            var i = visible.FindIndex(c => c.Id == id);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= visible.Count) return s;
            var (a, b) = (cards.IndexOf(visible[i]), cards.IndexOf(visible[j]));
            (cards[a], cards[b]) = (cards[b], cards[a]);
            return s with { Dashboard = s.Dashboard with { Cards = cards } };
        });
    }

    private void EditCard(string? id, Func<DashboardCardSetting, DashboardCardSetting> edit)
    {
        if (id is null) return;
        var r = _appearance.Update(s => s with { Dashboard = s.Dashboard with { Cards = s.Dashboard.Cards.Select(c => c.Id == id ? edit(c) : c).ToList() } });
        if (r.Message != "Saved." && r.Message != "No change.") Message = r.Message;
    }

    private void ApplyStackFilter()
    {
        Stacks.Clear();
        foreach (var row in _allStacks.Where(r => !OnlyNeedingAttention || r.Status != "Ready")) Stacks.Add(row);
    }

    private void RefreshDownloads()
    {
        DownloadRows.Clear();
        foreach (var j in _downloads.Jobs.OrderByDescending(j => j.QueuedAt).Take(5))
            DownloadRows.Add(new StatusRow(j.Request.DisplayName, j.StageLabel, j.Status, j.IsFailed ? StatusBrushes.Bad : j.IsActive ? StatusBrushes.Accent : StatusBrushes.Good));
    }

    private async Task RefreshAsync()
    {
        var ct = CancellationToken.None;
        var hw = await _hardware.GetAsync(ct);
        HardwareHeadline = _hardware.Assessment?.Headline ?? "Unknown";
        HardwareDetail = HardwareAssessor.Summary(hw);

        var status = await _ollama.GetStatusAsync(ct);
        RuntimeSummary = $"Ollama: {status.Summary}";
        RuntimeBrush = StatusBrushes.For(status.Health);

        LoadedModels.Clear();
        if (status.Health is ComponentHealth.Healthy or ComponentHealth.Degraded)
            foreach (var m in await _ollama.ListRunningAsync(ct))
                LoadedModels.Add(new StatusRow(m.Name, $"{m.GpuFraction:P0} GPU", $"{Bytes.Format(m.SizeBytes)} · context {m.ContextLength:N0}", m.GpuFraction >= 0.99 ? StatusBrushes.Good : StatusBrushes.Warn));

        Agents.Clear();
        foreach (var a in _agents)
        {
            var d = await a.DetectAsync(ct);
            Agents.Add(new StatusRow(a.DisplayName, d.Installed ? "Installed" : "Not installed", d.Summary, StatusBrushes.For(d.Health)));
        }

        _allStacks.Clear();
        foreach (var s in _stacks.All)
        {
            var r = await _validator.ValidateAsync(s, ct);
            var brush = r.Readiness switch
            {
                StackReadiness.Ready => StatusBrushes.Good,
                StackReadiness.ReadyWithLimitations => StatusBrushes.Warn,
                _ => StatusBrushes.Bad,
            };
            // Explain the status shown: prefer the failed check that produced it (e.g. the privacy check for "Security policy blocked").
            var why = (r.Readiness switch
            {
                StackReadiness.SecurityPolicyBlocked => r.Checks.FirstOrDefault(c => !c.Passed && c.Name is "Privacy policy" or "Network policy" or "Agent version"),
                StackReadiness.PermissionConflict => r.Checks.FirstOrDefault(c => !c.Passed && c.Name == "Permissions"),
                StackReadiness.RuntimeMissing or StackReadiness.ServiceUnavailable => r.Checks.FirstOrDefault(c => !c.Passed && c.Name == "Runtime"),
                _ => null,
            } ?? r.Checks.FirstOrDefault(c => !c.Passed && c.Severity == CheckSeverity.Error) ?? r.Checks.FirstOrDefault(c => !c.Passed))?.Detail ?? "All checks passed.";
            _allStacks.Add(new StatusRow(s.Name, r.Summary, why, brush));
        }
        ApplyStackFilter();

        RefreshDownloads();
        VerifiedRows.Clear();
        foreach (var v in _matrix.Entries.Where(e => e.Result == VerificationOutcome.SupportedAndVerified).OrderByDescending(e => e.VerifiedAt).Take(6))
            VerifiedRows.Add(new StatusRow(v.Label, "Supported and verified", $"{v.VerifiedAt:yyyy-MM-dd} · app {v.AppVersion}", StatusBrushes.Good));

        var running = _sessions.All.Count(x => x.State == SessionState.Running);
        SessionSummary = running == 0 ? "No agent sessions running." : $"{running} agent session(s) running.";
        var blocked = _audit.Recent(500).Count(e => e.Result is AuditResult.Blocked && e.Timestamp > DateTimeOffset.UtcNow.AddDays(-1));
        SecuritySummary = blocked == 0 ? "No blocked operations in the last 24 hours." : $"{blocked} operation(s) were blocked in the last 24 hours — see Security → Audit.";
        Message = $"Updated {DateTime.Now:HH:mm:ss}";
    }
}
