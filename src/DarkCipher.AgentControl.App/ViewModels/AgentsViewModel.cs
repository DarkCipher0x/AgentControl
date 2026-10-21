using System.Collections.ObjectModel;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed record AgentCard(IAgentProvider Provider, AgentInstallation Installation)
{
    public string Name => Provider.DisplayName;
    public string Vendor => Provider.Vendor;
    public string Summary => Installation.Summary;
    public string Path => Installation.ExecutablePath ?? "—";
    public string Notes => string.Join(" ", Installation.Notes);
    public IReadOnlyList<AgentModeInfo> Modes => Provider.Modes;
}

/// <summary>
/// Agents with a session provider (what each mode does and who enforces each permission), plus every agent tool in the
/// catalogue, so a missing or extra agent can be installed right here through the same plan → approval → queue path.
/// </summary>
public sealed class AgentsViewModel : PageViewModel, IContextActionProvider
{
    private readonly IEnumerable<IAgentProvider> _agents;
    private readonly IPermissionService _permissions;
    private readonly AgentToolCatalogueService _catalogue;
    private readonly ToolActions _actions;
    private AgentCard? _selected;
    private PermissionProfile? _profile;
    private AgentModeInfo? _mode;

    public AgentsViewModel(IEnumerable<IAgentProvider> agents, IPermissionService permissions, AgentToolCatalogueService catalogue, ToolActions actions)
    {
        _agents = agents;
        _permissions = permissions;
        _catalogue = catalogue;
        _actions = actions;
        _profile = permissions.Resolve(BuiltInPermissionProfiles.DefaultId);
        RescanCommand = new AsyncCommand(() => Guard(() => LoadAsync(true), "Rescan failed"));
        CopyPathCommand = new RelayCommand(p => { if (p is AgentCard { Installation.ExecutablePath: { } path }) Ui.Copy(path); }, p => p is AgentCard { Installation.ExecutablePath: not null });
        OpenFolderCommand = new AsyncCommand(p => p is AgentCard c ? _actions.OpenFolderAsync(c.Installation.ExecutablePath, c.Name) : Task.CompletedTask,
            p => p is AgentCard { Installation.ExecutablePath: not null });
        catalogue.Changed += (_, _) => Ui.OnUi(FillTools);
        actions.Status += (_, m) => Message = m;
    }

    public override string Title => "Agents";
    public override string Subtitle => "Agentic coding tools, how each one reaches its model, and who enforces each permission. Install more agent tools below; right-click any entry for more.";

    public ObservableCollection<AgentCard> Cards { get; } = [];
    public ObservableCollection<PermissionControl> Controls { get; } = [];
    public ObservableCollection<ToolRowViewModel> Tools { get; } = [];
    public IReadOnlyList<PermissionProfile> Profiles => _permissions.Profiles;
    public string ToolsNote => $"{Tools.Count(t => t.IsInstalled)} of {Tools.Count} agent tools installed. Tools marked Catalogue only can be installed and updated here; sessions use Claude Code and Codex.";

    public AgentCard? Selected { get => _selected; set { if (Set(ref _selected, value)) { Mode = (value is { Modes.Count: > 0 } ? value.Modes[0] : null); } } }
    public AgentModeInfo? Mode { get => _mode; set { if (Set(ref _mode, value)) Rebuild(); } }
    public PermissionProfile? Profile { get => _profile; set { if (Set(ref _profile, value)) Rebuild(); } }

    public ICommand RescanCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand OpenFolderCommand { get; }

    protected override async Task OnActivateAsync()
    {
        await LoadAsync(false);
        if (_catalogue.Cached.Count == 0) await _catalogue.RefreshAsync(false, CancellationToken.None);
        FillTools();
    }

    private async Task LoadAsync(bool rescan)
    {
        var keep = Selected?.Provider.Id;
        Cards.Clear();
        foreach (var a in _agents)
        {
            if (rescan) a.InvalidateDetection();
            Cards.Add(new AgentCard(a, await a.DetectAsync(CancellationToken.None)));
        }
        Selected = Cards.FirstOrDefault(c => c.Provider.Id == keep) ?? Cards.FirstOrDefault();
        if (rescan) await _catalogue.RefreshAsync(false, CancellationToken.None);
    }

    private void FillTools()
    {
        Tools.Clear();
        foreach (var e in _catalogue.Cached.Where(e => e.Descriptor.Kind == ToolKind.AgentTool && !e.Descriptor.Id.StartsWith("custom-", StringComparison.Ordinal))
                     .OrderBy(e => e.Descriptor.CatalogueOnly).ThenBy(e => e.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase))
            Tools.Add(new ToolRowViewModel(e, _actions));
        Raise(nameof(ToolsNote));
    }

    private void Rebuild()
    {
        Controls.Clear();
        if (Selected is null || Profile is null || Mode is null) return;
        foreach (var c in Selected.Provider.DescribeEnforcement(Profile, Mode.Mode)) Controls.Add(c);
    }

    public IReadOnlyList<MenuAction> ContextActions(object item)
    {
        if (item is ToolRowViewModel tool) return tool.MenuActions(inCatalogue: false);
        if (item is not AgentCard card) return [];
        var list = new List<MenuAction>
        {
            new("Rescan", RescanCommand, Glyph: ""),
        };
        // The same tool's catalogue actions (update, repair, uninstall, copy winget id …) follow, grouped.
        if (_catalogue.Cached.FirstOrDefault(e => e.Descriptor.Id == card.Provider.Id) is { } entry)
            list.AddRange(new ToolRowViewModel(entry, _actions).MenuActions(inCatalogue: false).Where(a => a.Header != "Copy executable path" && a.Header != "Open install folder"));
        list.Add(new("Open install folder", OpenFolderCommand, card, Group: "Open", Glyph: ""));
        list.Add(new("Copy executable path", CopyPathCommand, card, Group: "Copy", Glyph: ""));
        return list;
    }
}
