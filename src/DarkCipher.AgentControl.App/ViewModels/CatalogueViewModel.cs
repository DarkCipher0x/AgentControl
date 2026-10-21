using System.Collections.ObjectModel;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Appearance;
using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Templates;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.App.ViewModels;

/// <summary>
/// The catalogue and setup centre: agent tools, model runtimes, gateways and supporting tools with their verified
/// installation path, update checks, diagnose-first repair, and the user's custom tools. Every card and every
/// right-click menu uses the shared <see cref="ToolActions"/>, so installing from here or from a category page is the
/// same plan → approval → queue → test path. Models are on the Models page and templates on the Templates page.
/// </summary>
public sealed class CatalogueViewModel : PageViewModel, IContextActionProvider
{
    private readonly AgentToolCatalogueService _catalogue;
    private readonly AgentToolUpdateService _updates;
    private readonly CustomAgentRegistry _custom;
    private readonly DownloadManager _downloads;
    private readonly TemplateCatalogueService _templates;
    private readonly AppearanceSettingsService _appearance;
    private readonly ToolActions _actions;
    private ToolRowViewModel? _selected;
    private string _search = "";
    private string _customName = "", _customExe = "", _customVersionArgs = "--version";

    public CatalogueViewModel(AgentToolCatalogueService catalogue, AgentToolUpdateService updates, CustomAgentRegistry custom, DownloadManager downloads,
        TemplateCatalogueService templates, AppearanceSettingsService appearance, ToolActions actions, Navigator nav)
    {
        _catalogue = catalogue; _updates = updates; _custom = custom; _downloads = downloads; _templates = templates; _appearance = appearance; _actions = actions;
        RefreshCommand = new AsyncCommand(() => Guard(() => LoadAsync(false), "Refresh failed"));
        CheckUpdatesCommand = new AsyncCommand(() => Guard(CheckUpdatesAsync, "Update check failed"));
        RegisterCustomCommand = new RelayCommand(RegisterCustom);
        ValidateCustomCommand = new AsyncCommand(p => Guard(() => ValidateCustomAsync(p as ToolRowViewModel), "Validation failed"));
        RemoveCustomCommand = new RelayCommand(p => { if (p is ToolRowViewModel r && _custom.Remove(r.Id)) _ = LoadAsync(false); });
        GoModelsCommand = new RelayCommand(() => nav.Go("models"));
        GoTemplatesCommand = new RelayCommand(() => nav.Go("templates"));
        GoDownloadsCommand = new RelayCommand(() => nav.Go("downloads"));
        GoExtensionsCommand = new RelayCommand(() => nav.Go("extensions"));
        downloads.Changed += (_, _) => Ui.OnUi(RefreshQueue);
        catalogue.Changed += (_, _) => Ui.OnUi(() => Fill(_catalogue.Cached));
        actions.Status += (_, m) => Message = m;
        appearance.Changed += (_, _) => Ui.OnUi(() => Raise(nameof(ShowAdvanced)));
    }

    public override string Title => "Catalogue";
    public override string Subtitle => "Discover, install, update and repair agent tools, runtimes, gateways and supporting tools through verified sources. Nothing is installed without a plan you approve. Right-click any tool for more.";

    public ObservableCollection<ToolRowViewModel> AgentTools { get; } = [];
    public ObservableCollection<ToolRowViewModel> Runtimes { get; } = [];
    public ObservableCollection<ToolRowViewModel> Gateways { get; } = [];
    public ObservableCollection<ToolRowViewModel> SupportingTools { get; } = [];
    public ObservableCollection<ToolRowViewModel> Available { get; } = [];
    public ObservableCollection<ToolRowViewModel> Installed { get; } = [];
    public ObservableCollection<ToolRowViewModel> Updates { get; } = [];
    public ObservableCollection<ToolRowViewModel> CustomTools { get; } = [];
    public ObservableCollection<DownloadJob> Queue { get; } = [];
    public ToolRowViewModel? Selected { get => _selected; set => Set(ref _selected, value); }
    public string TemplateSummary => $"{_templates.All.Count} templates ({_templates.All.Count(t => t.Origin == Domain.Templates.TemplateOrigin.BuiltIn)} built-in).";
    public bool ShowAdvanced => _appearance.Current.ShowAdvancedInformation;
    public string Search { get => _search; set { if (Set(ref _search, value)) Fill(_catalogue.Cached); } }
    public string Counts { get; private set; } = "";

    public string CustomName { get => _customName; set => Set(ref _customName, value); }
    public string CustomExecutable { get => _customExe; set => Set(ref _customExe, value); }
    public string CustomVersionArgs { get => _customVersionArgs; set => Set(ref _customVersionArgs, value); }

    public ICommand RefreshCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand RegisterCustomCommand { get; }
    public ICommand ValidateCustomCommand { get; }
    public ICommand RemoveCustomCommand { get; }
    public ICommand GoModelsCommand { get; }
    public ICommand GoTemplatesCommand { get; }
    public ICommand GoDownloadsCommand { get; }
    public ICommand GoExtensionsCommand { get; }

    protected override Task OnActivateAsync() => LoadAsync(false);

    private async Task LoadAsync(bool checkAvailable)
    {
        Message = "Detecting installed tools…";
        var entries = await _catalogue.RefreshAsync(checkAvailable, CancellationToken.None);
        Fill(entries);
        RefreshQueue();
        Raise(nameof(TemplateSummary));
        Message = $"{entries.Count(e => e.Status.Installed)} of {entries.Count} installed. Detected {DateTime.Now:HH:mm:ss}" + (checkAvailable ? " (with available versions)." : ".");
    }

    private bool Matches(ToolEntry e) => Search.Length == 0
        || e.Descriptor.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase)
        || e.Descriptor.Publisher.Contains(Search, StringComparison.OrdinalIgnoreCase)
        || e.Descriptor.Description.Contains(Search, StringComparison.OrdinalIgnoreCase)
        || (e.Descriptor.Source?.PackageId.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false);

    private void Fill(IReadOnlyList<ToolEntry> entries)
    {
        var keep = Selected?.Id;
        AgentTools.Clear(); Runtimes.Clear(); Gateways.Clear(); SupportingTools.Clear(); Available.Clear(); Installed.Clear(); Updates.Clear(); CustomTools.Clear();
        foreach (var e in entries.Where(Matches).OrderBy(e => e.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var row = new ToolRowViewModel(e, _actions);
            if (row.IsCustom) CustomTools.Add(row);
            else (e.Descriptor.Kind switch { ToolKind.ModelRuntime => Runtimes, ToolKind.Gateway => Gateways, ToolKind.SupportingTool => SupportingTools, _ => AgentTools }).Add(row);
            if (e.Status.Installed) Installed.Add(row);
            else if (row.CanInstall) Available.Add(row);
            if (e.UpdateAvailable) Updates.Add(row);
        }
        Selected = AgentTools.Concat(Runtimes).Concat(Gateways).Concat(SupportingTools).Concat(CustomTools).FirstOrDefault(r => r.Id == keep);
        Counts = $"Agent tools {AgentTools.Count} · runtimes {Runtimes.Count} · gateways {Gateways.Count} · supporting {SupportingTools.Count} · available to install {Available.Count}";
        Raise(nameof(Counts));
    }

    private void RefreshQueue()
    {
        Queue.Clear();
        foreach (var j in _downloads.Jobs.OrderByDescending(j => j.QueuedAt).Take(8)) Queue.Add(j);
    }

    private async Task CheckUpdatesAsync()
    {
        Message = "Asking winget for the newest versions…";
        var updates = await _updates.CheckAsync(CancellationToken.None);
        Fill(_catalogue.Cached);
        Message = updates.Count == 0 ? "Everything installed is up to date (according to the winget source)." : $"{updates.Count} update(s) available.";
    }

    public IReadOnlyList<MenuAction> ContextActions(object item) => item switch
    {
        ToolRowViewModel { IsCustom: true } r =>
        [
            new("Run validation", ValidateCustomCommand, r, Glyph: ""),
            new("Details", r.DetailsCommand, Glyph: ""),
            new("Open folder", r.OpenFolderCommand, Group: "Open", Glyph: ""),
            new("Copy executable path", r.CopyPathCommand, Group: "Copy", Glyph: ""),
            new("Remove", RemoveCustomCommand, r, Danger: true, Glyph: ""),
        ],
        ToolRowViewModel r => r.MenuActions(inCatalogue: true),
        _ => [],
    };

    private void RegisterCustom()
    {
        var id = "custom-" + new string(CustomName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var r = _custom.Register(new CustomAgentDefinition
        {
            Id = id.Length > 47 ? id[..47] : id,
            DisplayName = CustomName.Trim(),
            Executable = CustomExecutable.Trim().Trim('"'),
            VersionArguments = CustomVersionArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries),
        });
        Message = r.Message;
        if (r.Success) { CustomName = ""; CustomExecutable = ""; _ = LoadAsync(false); }
    }

    private async Task ValidateCustomAsync(ToolRowViewModel? row)
    {
        if (row is null) return;
        var provider = _catalogue.Find(row.Id);
        if (provider is null) return;
        var status = await provider.DetectAsync(CancellationToken.None);
        Message = _custom.RecordValidation(row.Id, status).Message;
        await LoadAsync(false);
    }
}
