using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Ollama;

namespace DarkCipher.AgentControl.App.ViewModels;

/// <summary>Running services (Ollama, loaded models, gateways) plus every runtime and gateway in the catalogue, installable
/// from here through the same plan → approval → queue path as the Catalogue.</summary>
public sealed class ServicesViewModel : PageViewModel, IContextActionProvider
{
    private readonly OllamaRuntime _ollama;
    private readonly AgentToolCatalogueService _catalogue;
    private readonly ToolActions _actions;
    private readonly OllamaLauncher _launcher;
    private RunningModel? _selected;
    private string _summary = "", _version = "", _endpoint = "", _install = "", _classification = "";
    private Brush _brush = StatusBrushes.Muted;

    private readonly Extensions.ExtensionContributionRegistry _contributions;

    public ServicesViewModel(OllamaRuntime ollama, Extensions.ExtensionContributionRegistry contributions, AgentToolCatalogueService catalogue, ToolActions actions, OllamaLauncher launcher)
    {
        _ollama = ollama;
        _contributions = contributions;
        _catalogue = catalogue;
        _actions = actions;
        _launcher = launcher;
        RunChecksCommand = new AsyncCommand(() => Guard(RunChecksAsync, "Health checks failed"), () => _contributions.HealthChecks.Count > 0);
        RefreshCommand = new AsyncCommand(() => Guard(async () => { await LoadAsync(); await _catalogue.RefreshAsync(false, CancellationToken.None); }, "Refresh failed"));
        UnloadCommand = new AsyncCommand(() => Guard(async () => { Message = (await _ollama.UnloadAsync(Selected!.Name, CancellationToken.None)).Message; await LoadAsync(); }, "Unload failed"), () => Selected is not null);
        StartOllamaCommand = new AsyncCommand(() => Guard(StartOllamaAsync, "Start failed"));
        UnloadModelCommand = new AsyncCommand(p => Guard(async () => { if (p is RunningModel m) { Message = (await _ollama.UnloadAsync(m.Name, CancellationToken.None)).Message; await LoadAsync(); } }, "Unload failed"));
        CopyModelNameCommand = new RelayCommand(p => { if (p is RunningModel m) Ui.Copy(m.Name); });
        catalogue.Changed += (_, _) => Ui.OnUi(FillTools);
        actions.Status += (_, m) => Message = m;
    }

    public override string Title => "Services";
    public override string Subtitle => "Local runtimes and gateways: what is running, and everything you can install. This app never stops or reconfigures services it does not own; it starts an installed Ollama only when you ask. Right-click any entry for more.";

    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Version { get => _version; private set => Set(ref _version, value); }
    public string Endpoint { get => _endpoint; private set => Set(ref _endpoint, value); }
    public string InstallLocation { get => _install; private set => Set(ref _install, value); }
    public string Classification { get => _classification; private set => Set(ref _classification, value); }
    public Brush HealthBrush { get => _brush; private set => Set(ref _brush, value); }
    public ObservableCollection<RunningModel> Running { get; } = [];
    public ObservableCollection<ToolRowViewModel> Runtimes { get; } = [];
    public ObservableCollection<ToolRowViewModel> Gateways { get; } = [];
    public RunningModel? Selected { get => _selected; set => Set(ref _selected, value); }
    public IReadOnlyList<string> GatewayLimitations => GatewayProbe.AnthropicGatewayLimitations;
    public IReadOnlyList<string> PlannedRuntimes { get; } =
    [
        "Stacks on LM Studio, llama.cpp, Jan or llamafile: Planned. They can be installed, updated and detected now; stacks still run on Ollama.",
        "vLLM: Planned (Linux/WSL hosts; no per-user Windows package).",
        "LocalAI: Planned (no reviewed winget package).",
        "Generic OpenAI-/Anthropic-compatible endpoints: Planned (will require explicit endpoint approval).",
        "NPU runtimes (Foundry Local / ONNX Runtime): Planned; NPUs are detected only.",
    ];

    public ICommand RefreshCommand { get; }
    public ICommand UnloadCommand { get; }
    public ICommand RunChecksCommand { get; }
    public ICommand StartOllamaCommand { get; }
    public ICommand UnloadModelCommand { get; }
    public ICommand CopyModelNameCommand { get; }
    public ObservableCollection<StatusRow> ExtensionChecks { get; } = [];
    public string ExtensionChecksNote => _contributions.HealthChecks.Count == 0 ? "No extension has registered a health check." : $"{_contributions.HealthChecks.Count} check(s) from extensions (5 s each, isolated).";

    protected override async Task OnActivateAsync()
    {
        await LoadAsync();
        if (_catalogue.Cached.Count == 0) await _catalogue.RefreshAsync(false, CancellationToken.None);
        FillTools();
    }

    private void FillTools()
    {
        Runtimes.Clear();
        Gateways.Clear();
        foreach (var e in _catalogue.Cached.OrderBy(e => e.Descriptor.CatalogueOnly).ThenBy(e => e.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (e.Descriptor.Kind == ToolKind.ModelRuntime) Runtimes.Add(new ToolRowViewModel(e, _actions));
            else if (e.Descriptor.Kind == ToolKind.Gateway) Gateways.Add(new ToolRowViewModel(e, _actions));
        }
    }

    private async Task StartOllamaAsync()
    {
        Message = "Starting Ollama…";
        var r = await _launcher.EnsureRunningAsync(CancellationToken.None);
        Message = r.Message;
        await LoadAsync();
        await _catalogue.RefreshAsync(false, CancellationToken.None);
    }

    public IReadOnlyList<MenuAction> ContextActions(object item) => item switch
    {
        ToolRowViewModel { Id: "ollama", IsInstalled: true } t => [new("Start Ollama", StartOllamaCommand, Glyph: ""), .. t.MenuActions(inCatalogue: false)],
        ToolRowViewModel t => t.MenuActions(inCatalogue: false),
        RunningModel m =>
        [
            new("Unload from memory", UnloadModelCommand, m, Glyph: ""),
            new("Copy model name", CopyModelNameCommand, m, Group: "Copy", Glyph: ""),
        ],
        _ => [],
    };

    private async Task RunChecksAsync()
    {
        ExtensionChecks.Clear();
        foreach (var (ext, title, health, detail) in await _contributions.RunHealthChecksAsync())
            ExtensionChecks.Add(new StatusRow($"{title} ({ext})", health.ToString(), detail,
                health == Sdk.SdkHealth.Healthy ? StatusBrushes.Good : health == Sdk.SdkHealth.Degraded ? StatusBrushes.Warn : StatusBrushes.Bad));
        Raise(nameof(ExtensionChecksNote));
    }

    private async Task LoadAsync()
    {
        var s = await _ollama.GetStatusAsync(CancellationToken.None);
        Summary = s.Summary;
        Version = s.Version ?? "—";
        Endpoint = s.Endpoint?.BaseUri.ToString() ?? "—";
        InstallLocation = s.InstallLocation ?? "not found";
        Classification = s.Endpoint is null ? "—" : $"{s.Connection} · {s.Endpoint.Class} · {s.Endpoint.Auth} · {s.Endpoint.Trust}";
        HealthBrush = StatusBrushes.For(s.Health);
        Running.Clear();
        if (s.Health is ComponentHealth.Healthy or ComponentHealth.Degraded)
            foreach (var m in await _ollama.ListRunningAsync(CancellationToken.None)) Running.Add(m);
    }
}
