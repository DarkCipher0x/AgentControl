using System.Collections.ObjectModel;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Extensions;
using DarkCipher.AgentControl.Sdk;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed record ContributionRow(string ExtensionId, string Kind, string Title, Func<CancellationToken, Task<string>> Run);

/// <summary>Installed extensions, the bundled gallery (data-only extensions shipped with the app, one click to review and
/// install), and contributed commands and panels.</summary>
public sealed class ExtensionsViewModel : PageViewModel, IContextActionProvider
{
    private readonly ExtensionHost _host;
    private readonly ExtensionGallery _gallery;
    private readonly ISecurityPolicyService _policy;
    private readonly IProcessExecutor _processes;
    private ExtensionRecord? _selected;
    private ContributionRow? _selectedContribution;

    public ExtensionsViewModel(ExtensionHost host, ExtensionGallery gallery, ISecurityPolicyService policy, IProcessExecutor processes, Navigator nav)
    {
        _host = host;
        _gallery = gallery;
        _policy = policy;
        _processes = processes;
        host.Changed += (_, _) => Ui.OnUi(Reload);
        host.Contributions.Changed += (_, _) => Ui.OnUi(Reload);
        policy.Changed += (_, _) => Ui.OnUi(() => Raise(nameof(PolicyNote)));
        InstallCommand = new RelayCommand(InstallFromFile);
        EnableCommand = new RelayCommand(p => { if (Target(p) is { } e) Message = _host.SetEnabled(e.Id, true).Message; }, p => Target(p) is { Trust: not TrustLevel.BuiltIn, Enabled: false });
        DisableCommand = new RelayCommand(p => { if (Target(p) is { } e) Message = _host.SetEnabled(e.Id, false).Message; }, p => Target(p) is { Trust: not TrustLevel.BuiltIn, Enabled: true });
        RemoveCommand = new RelayCommand(p => Remove(Target(p)), p => Target(p) is { Trust: not TrustLevel.BuiltIn });
        DetailsCommand = new RelayCommand(p => Details(Target(p)), p => Target(p) is not null);
        OpenFolderCommand = new AsyncCommand(p => OpenFolderAsync(Target(p)?.InstallPath), p => Target(p) is { InstallPath: not null });
        CopyIdCommand = new RelayCommand(p => { if (Target(p) is { } e) Ui.Copy(e.Id); }, p => Target(p) is not null);
        RunCommand = new AsyncCommand(RunAsync, () => SelectedContribution is not null);
        InstallGalleryCommand = new RelayCommand(p => InstallGallery(p as GalleryItem), p => p is GalleryItem { CanInstall: true });
        GalleryDetailsCommand = new RelayCommand(p => { if (p is GalleryItem g) GalleryDetails(g); });
        GoStacksCommand = new RelayCommand(() => nav.Go("stacks"));
        GoModelsCommand = new RelayCommand(() => nav.Go("models"));
    }

    public override string Title => "Extensions";
    public override string Subtitle => "Add stack templates, model lists, agent providers and more. Install bundled extensions from the Gallery, or a package from a file. Every package is reviewed before install; failures never block start-up. Right-click any extension for more.";

    public ObservableCollection<ExtensionRecord> Extensions { get; } = [];
    public ObservableCollection<GalleryItem> Gallery { get; } = [];
    public ObservableCollection<ContributionRow> Contributions { get; } = [];
    public ExtensionRecord? Selected { get => _selected; set => Set(ref _selected, value); }
    public ContributionRow? SelectedContribution { get => _selectedContribution; set => Set(ref _selectedContribution, value); }
    public string GalleryNote => Gallery.Count == 0
        ? "No bundled extensions were found next to the app."
        : $"{Gallery.Count} bundled extensions from DarkCipher Systems: data only (they run no code), already on this PC (nothing is downloaded). {Gallery.Count(g => g.Installed)} installed.";

    public string PolicyNote
    {
        get
        {
            var p = _policy.Current;
            if (p.SafeMode) return "Safe Mode is on: only built-in components are loaded.";
            if (p.DisableThirdPartyExtensions) return "Third-party extensions are disabled (Security → Policy). Packages can be reviewed and installed but will not load. Bundled gallery extensions still load.";
            return p.DevelopmentMode
                ? "Third-party extensions enabled. Development Mode is ON: code extensions load in-process WITHOUT isolation."
                : "Third-party extensions enabled. Only data-only (declarative) extensions load; code extensions require Development Mode.";
        }
    }

    public ICommand InstallCommand { get; }
    public ICommand EnableCommand { get; }
    public ICommand DisableCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyIdCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand InstallGalleryCommand { get; }
    public ICommand GalleryDetailsCommand { get; }
    public ICommand GoStacksCommand { get; }
    public ICommand GoModelsCommand { get; }

    /// <summary>Right-click passes the row; toolbar buttons act on the selection.</summary>
    private ExtensionRecord? Target(object? p) => p as ExtensionRecord ?? Selected;

    protected override Task OnActivateAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        var keep = Selected?.Id;
        Extensions.Clear();
        foreach (var e in _host.Extensions) Extensions.Add(e);
        Selected = Extensions.FirstOrDefault(e => e.Id == keep);
        Gallery.Clear();
        foreach (var g in _gallery.Items()) Gallery.Add(g);
        Raise(nameof(GalleryNote));
        Contributions.Clear();
        foreach (var (ext, c) in _host.Contributions.Commands) Contributions.Add(new ContributionRow(ext, "Command", c.Title, c.Execute));
        foreach (var (ext, p) in _host.Contributions.Panels)
            Contributions.Add(new ContributionRow(ext, "Dashboard panel", p.Title, async ct => string.Join("\n", (await p.GetRows(ct)).Select(kv => $"{kv.Key}: {kv.Value}"))));
    }

    private void InstallFromFile()
    {
        if (Dialogs.Headless) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "DarkCipher extension (*.dcxext)|*.dcxext" };
        if (dlg.ShowDialog() != true) return;
        ReviewAndInstall(_host.Review(dlg.FileName));
    }

    private void InstallGallery(GalleryItem? item)
    {
        if (item is null) return;
        var (review, errors) = _gallery.Prepare(item);
        if (review is null) { Dialogs.Show("Cannot install", $"'{item.Name}' could not be prepared.", errors.Select(e => "✖ " + e).ToList(), ["Close"], "Bad"); return; }
        ReviewAndInstall(review);
    }

    /// <summary>The same review dialog for every package: identity, trust, isolation, permissions, endpoints, warnings.</summary>
    private void ReviewAndInstall(InstallReview review)
    {
        var details = new List<string>
        {
            $"Id: {review.Manifest.Id}   Version: {review.Manifest.Version}   Publisher: {review.Manifest.Publisher}",
            $"Trust: {review.Trust}   Signature: {review.SignatureStatus}",
            $"Isolation: {review.Isolation}",
            $"Licence: {review.Manifest.License}   Source: {review.Manifest.Source ?? "not stated"}",
        };
        details.AddRange(review.Permissions.Select(p => $"Permission {p.Permission}: {p.Meaning} — risk {p.Risk}"));
        if (review.Permissions.Count == 0) details.Add("Permissions: none requested.");
        details.AddRange(review.Manifest.NetworkEndpoints.Select(e => "Network endpoint: " + e));
        details.AddRange(review.Warnings.Select(w => "⚠ " + w));
        if (!review.CanInstall)
        {
            details.InsertRange(0, review.Errors.Select(e => "✖ " + e));
            Dialogs.Show("Extension rejected", "This package cannot be installed.", details, ["Close"], "Bad");
            _host.DiscardReview(review);
            return;
        }
        if (!Dialogs.Confirm("Review extension", $"Install '{review.Manifest.Name}'" + (review.Permissions.Count > 0 ? " and grant the permissions listed?" : "?"), details, "Install",
                danger: !review.Manifest.IsDeclarative))
        {
            _host.DiscardReview(review);
            return;
        }
        Message = _host.Install(review, review.Manifest.Permissions).Message;
        Reload();
    }

    private void Remove(ExtensionRecord? e)
    {
        if (e is null) return;
        if (!Dialogs.Confirm("Remove extension", $"Remove '{e.Name}'? Its files and settings are deleted.", null, "Remove", danger: true)) return;
        Message = _host.Remove(e.Id).Message;
    }

    private static void Details(ExtensionRecord? e)
    {
        if (e is null) return;
        var lines = new List<string>
        {
            $"{e.Name} {e.Version} — {e.Publisher}",
            e.Description,
            "",
            $"Id: {e.Id}",
            $"Trust: {e.Trust}   Signature: {e.SignatureStatus}",
            $"State: {e.State} — {e.StateReason}",
            $"Isolation: {e.Isolation}",
            $"Types: {string.Join(", ", e.Types)}",
            $"Requested permissions: {(e.RequestedPermissions.Count == 0 ? "none" : string.Join(", ", e.RequestedPermissions))}",
            $"Granted permissions: {(e.GrantedPermissions.Count == 0 ? "none" : string.Join(", ", e.GrantedPermissions))}",
            $"Licence: {e.License ?? "—"}   Source: {e.Source ?? "—"}",
            $"Installed at: {e.InstallPath ?? "—"}",
        };
        Dialogs.ShowText(e.Name, string.Join(Environment.NewLine, lines));
    }

    private static void GalleryDetails(GalleryItem g)
    {
        var m = g.Manifest;
        var lines = new List<string>
        {
            $"{m.Name} {m.Version} — {m.Publisher}",
            m.Description,
            "",
            $"Id: {m.Id}",
            $"Contains: {g.Kind}",
            "Isolation: data only, runs no code.",
            "Permissions: none.",
            $"Licence: {m.License}",
            $"State: {g.State}",
        };
        lines.AddRange(m.Contributes.StackTemplates.Select(t => "Stack templates file: " + t));
        lines.AddRange(m.Contributes.ModelCatalogs.Select(t => "Model list file: " + t));
        Dialogs.ShowText(m.Name, string.Join(Environment.NewLine, lines));
    }

    private async Task OpenFolderAsync(string? path)
    {
        if (path is null || !Directory.Exists(path)) { Message = "The folder no longer exists."; return; }
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var r = await _processes.RunAsync(new ProcessSpec { Executable = explorer, Arguments = [path], Timeout = TimeSpan.FromSeconds(10), Purpose = "Open extension folder" }, CancellationToken.None);
        if (r.Outcome == ProcessOutcome.Blocked) Message = r.Message;
    }

    public IReadOnlyList<MenuAction> ContextActions(object item) => item switch
    {
        GalleryItem g =>
        [
            new(g.UpdateAvailable ? "Update…" : "Install…", InstallGalleryCommand, g, Glyph: ""),
            new("Details", GalleryDetailsCommand, g, Glyph: ""),
            new("Open Stacks", GoStacksCommand, Group: "Open", Glyph: ""),
            new("Open Models", GoModelsCommand, Group: "Open", Glyph: ""),
        ],
        ExtensionRecord e =>
        [
            new("Enable", EnableCommand, e, Glyph: ""),
            new("Disable", DisableCommand, e, Glyph: ""),
            new("Details", DetailsCommand, e, Glyph: ""),
            new("Open install folder", OpenFolderCommand, e, Group: "Open", Glyph: ""),
            new("Copy id", CopyIdCommand, e, Group: "Copy", Glyph: ""),
            new("Remove…", RemoveCommand, e, Danger: true, Glyph: ""),
        ],
        ContributionRow c => [new("Run", RunCommand, Glyph: "")],
        _ => [],
    };

    private async Task RunAsync()
    {
        var c = SelectedContribution!;
        var (ok, result) = await ExtensionContributionRegistry.InvokeIsolatedAsync(c.Run, TimeSpan.FromSeconds(15));
        Dialogs.ShowText($"{c.Title} ({c.ExtensionId})", ok ? result : "Failed: " + result);
    }
}
