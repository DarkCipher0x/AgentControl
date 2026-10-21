using System.Collections.ObjectModel;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.ViewModels;
using DarkCipher.AgentControl.Application.Appearance;
using Microsoft.Extensions.DependencyInjection;

namespace DarkCipher.AgentControl.App.Shell;

public sealed record NavItem(string Key, string Title, string Glyph, string Group, Type ViewModelType, string Hint)
{
    public bool FirstInGroup { get; init; }
    /// <summary>Shown only when Settings → Navigation → "Show experimental sections" is on.</summary>
    public bool Experimental { get; init; }
}

/// <summary>Owns the navigation list and the current page. Views are resolved by DataTemplate from the view model type.
/// The sidebar follows the appearance settings (pinned, hidden, developer and experimental sections); pages holding
/// security or active-session controls can never be hidden, and any page stays reachable by navigation.</summary>
public sealed class Navigator : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly AppearanceSettingsService _appearance;
    private PageViewModel? _current;
    private NavItem? _selected;
    private string _status = "Ready";

    public Navigator(IServiceProvider services, AppearanceSettingsService appearance)
    {
        _services = services;
        _appearance = appearance;
        Rebuild();
        appearance.Changed += (_, _) => Ui.OnUi(Rebuild);
    }

    /// <summary>The sidebar, in display order (pinned first).</summary>
    public ObservableCollection<NavItem> Items { get; } = [];

    /// <summary>Every destination, including hidden ones.</summary>
    public static IReadOnlyList<NavItem> All => Raw;

    /// <summary>Last destinations visited (newest first, current page excluded), shown in the status bar.</summary>
    public ObservableCollection<NavItem> Recent { get; } = [];

    public event EventHandler? ItemsRebuilt;

    public void Rebuild()
    {
        var s = _appearance.Current;
        var visible = Raw.Where(n => !s.HiddenPages.Contains(n.Key) && (s.ShowDeveloperSections || n.Key != "developer")
            && (s.ShowExperimentalSections || !n.Experimental)).ToList();
        var pinned = s.PinnedPages.Select(k => visible.FirstOrDefault(n => n.Key == k)).OfType<NavItem>().Select(n => n with { Group = "PINNED" }).ToList();
        var ordered = pinned.Concat(visible.Where(n => !s.PinnedPages.Contains(n.Key))).ToList();
        Items.Clear();
        for (var i = 0; i < ordered.Count; i++) Items.Add(ordered[i] with { FirstInGroup = i == 0 || ordered[i - 1].Group != ordered[i].Group });
        ItemsRebuilt?.Invoke(this, EventArgs.Empty);
    }

    private static NavItem[]? _raw;
    private static NavItem[] Raw => _raw ??= Build();

    private static NavItem[] Build()
    {
        var list = Original.ToList();
        // Catalogue and Templates sit in BUILD next to Agents/Models/Stacks (Segoe Fluent: Shop, Document).
        list.Insert(list.FindIndex(n => n.Key == "agents"), new("catalogue", "Catalogue", "", "BUILD", typeof(CatalogueViewModel), "Install, update and repair agent tools, runtimes and gateways"));
        list.Insert(list.FindIndex(n => n.Key == "stacks"), new("templates", "Templates", "", "BUILD", typeof(TemplatesViewModel), "Turn installed components into a working stack"));
        return [.. list];
    }

    private static readonly NavItem[] Original =
    [
        new("dashboard", "Dashboard", "", "OVERVIEW", typeof(DashboardViewModel), "Health of your stacks, runtimes and agents"),
        new("setup", "Quick Setup", "", "OVERVIEW", typeof(SetupViewModel), "Get My Local Coding AI Ready"),
        new("agents", "Agents", "", "BUILD", typeof(AgentsViewModel), "Claude Code, Codex and future agent CLIs"),
        new("models", "Models", "", "BUILD", typeof(ModelsViewModel), "Installed models, recommendations, Hugging Face, compatibility"),
        new("stacks", "Stacks", "", "BUILD", typeof(StacksViewModel), "Agent + runtime + model + permissions"),
        new("projects", "Projects", "", "WORK", typeof(ProjectsViewModel), "Folders you allow agents to work in"),
        new("sessions", "Sessions", "", "WORK", typeof(SessionsViewModel), "Start, stop and track agent sessions"),
        new("services", "Services", "", "SYSTEM", typeof(ServicesViewModel), "Runtimes, endpoints, loaded models, gateways"),
        new("hardware", "Hardware", "", "SYSTEM", typeof(HardwareViewModel), "What this PC can run, and why"),
        new("downloads", "Downloads", "", "SYSTEM", typeof(DownloadsViewModel), "Model downloads and verification"),
        new("extensions", "Extensions", "", "PLATFORM", typeof(ExtensionsViewModel), "Install, review and manage extensions"),
        new("developer", "Developer / SDK", "", "PLATFORM", typeof(DeveloperViewModel), "SDK, tooling and Development Mode"),
        new("security", "Security", "", "CONTROL", typeof(SecurityViewModel), "Policy, protected folders, credentials, audit"),
        new("logs", "Logs", "", "CONTROL", typeof(LogsViewModel), "Application log"),
        new("settings", "Settings", "", "CONTROL", typeof(SettingsViewModel), "Preferences"),
    ];

    public PageViewModel? Current { get => _current; private set => Set(ref _current, value); }

    public NavItem? Selected
    {
        get => _selected;
        set
        {
            if (value is null || value.Key == _selected?.Key) return;
            var previous = _selected;
            if (!Set(ref _selected, value)) return;
            var vm = (PageViewModel)_services.GetRequiredService(value.ViewModelType);
            Current = vm;
            CurrentTitle = value.Title;
            if (previous is not null)
            {
                var existing = Recent.FirstOrDefault(r => r.Key == previous.Key);
                if (existing is not null) Recent.Remove(existing);
                Recent.Insert(0, Raw.First(r => r.Key == previous.Key));
            }
            var self = Recent.FirstOrDefault(r => r.Key == value.Key);
            if (self is not null) Recent.Remove(self);
            while (Recent.Count > 3) Recent.RemoveAt(Recent.Count - 1);
            _ = vm.ActivateSafeAsync();
        }
    }

    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>Title of the page in view (used to name the component in error reports).</summary>
    public static string? CurrentTitle { get; private set; }

    /// <summary>Navigates by key, including to pages hidden from the sidebar.</summary>
    public void Go(string key) => Selected = Items.FirstOrDefault(i => i.Key == key) ?? Raw.FirstOrDefault(i => i.Key == key) ?? Raw[0];
}
