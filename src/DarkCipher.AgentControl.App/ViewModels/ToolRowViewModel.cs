using System.Windows.Input;
using System.Windows.Media;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.App.ViewModels;

/// <summary>
/// One catalogue tool (agent tool, runtime, gateway or supporting tool), with its own commands so the same card and the
/// same right-click menu work on every page that lists tools (Catalogue, Agents, Services, Dashboard).
/// </summary>
public sealed class ToolRowViewModel : ObservableObject
{
    private readonly ToolActions _actions;

    public ToolRowViewModel(ToolEntry entry, ToolActions actions)
    {
        Entry = entry;
        _actions = actions;
        InstallCommand = new AsyncCommand(() => _actions.RunAsync(Id, InstallOperation.Install), () => CanInstall);
        UpdateCommand = new AsyncCommand(() => _actions.RunAsync(Id, InstallOperation.Update), () => CanUpdate);
        UninstallCommand = new AsyncCommand(() => _actions.RunAsync(Id, InstallOperation.Uninstall), () => CanUninstall);
        RepairCommand = new AsyncCommand(() => _actions.RepairAsync(Id), () => CanRepair);
        PlanCommand = new AsyncCommand(() => _actions.ShowPlanAsync(Id, CanInstall ? InstallOperation.Install : InstallOperation.Update), () => CanInstall || CanUpdate);
        HomepageCommand = new AsyncCommand(() => _actions.OpenHomepageAsync(D), () => D.Source?.Homepage is not null);
        OpenFolderCommand = new AsyncCommand(() => _actions.OpenFolderAsync(Entry.Status.ExecutablePath, D.DisplayName), () => Entry.Status.ExecutablePath is not null);
        DetailsCommand = new RelayCommand(() => _actions.ShowDetails(Entry));
        CopyNameCommand = new RelayCommand(() => Ui.Copy(D.DisplayName));
        CopyPackageIdCommand = new RelayCommand(() => Ui.Copy(D.Source!.PackageId), () => D.Source is not null);
        CopyPathCommand = new RelayCommand(() => Ui.Copy(Entry.Status.ExecutablePath!), () => Entry.Status.ExecutablePath is not null);
        CopyInstallCommandCommand = new RelayCommand(() => Ui.Copy($"winget install --id {D.Source!.PackageId} --exact --source winget --scope user"), () => D.Source is not null);
        ShowInCatalogueCommand = new RelayCommand(_actions.GoCatalogue);
        ShowDownloadsCommand = new RelayCommand(_actions.GoDownloads);
    }

    public ToolEntry Entry { get; }
    public AgentToolDescriptor D => Entry.Descriptor;
    public string Id => D.Id;
    public string Name => D.DisplayName;
    public string Publisher => D.Publisher;
    public string Description => D.Description;
    public string Kind => D.Kind switch { ToolKind.ModelRuntime => "Model runtime", ToolKind.Gateway => "Gateway", ToolKind.SupportingTool => "Supporting tool", _ => "Agent tool" };
    public string State => Entry.Status.State switch
    {
        ToolInstallState.Installed when Entry.UpdateAvailable => "Update available",
        ToolInstallState.Installed when D.Kind == ToolKind.Gateway && Entry.Status.Health == ComponentHealth.Unavailable => "Installed · not running",
        ToolInstallState.Installed => "Installed",
        ToolInstallState.Broken => "Needs repair",
        ToolInstallState.NotInstalled => "Not installed",
        _ => "Unknown",
    };
    public Brush StateBrush => Entry.Status.State switch
    {
        ToolInstallState.Installed when Entry.UpdateAvailable => StatusBrushes.Info,
        ToolInstallState.Installed when D.Kind == ToolKind.Gateway && Entry.Status.Health == ComponentHealth.Unavailable => StatusBrushes.Warn,
        ToolInstallState.Installed => StatusBrushes.Good,
        ToolInstallState.Broken => StatusBrushes.Bad,
        _ => StatusBrushes.Muted,
    };
    public string InstalledVersion => Entry.Status.Version ?? "—";
    public string AvailableVersion => Entry.AvailableVersion ?? "not checked";
    public string VerifiedVersion => D.LatestVerifiedVersion ?? "not yet verified";
    public string VersionVerdict => Entry.VersionVerdict;
    public string Source => D.Source?.Display ?? D.InstallMethod;
    public string Licence => D.Source?.Licence ?? "—";
    public string Protocols => D.SupportedProtocols.Count == 0 ? "—" : string.Join(", ", D.SupportedProtocols);
    public string Trust => Entry.TrustLabel;
    public string Summary => Entry.Status.Summary;
    public string CheckedAt => $"checked {Entry.CheckedAt.ToLocalTime():HH:mm}";
    public bool IsInstalled => Entry.Status.Installed;
    public bool CatalogueOnly => D.CatalogueOnly;
    public string? PartOfLabel => D.PartOfToolId is { } p ? "Part of " + _actions.DisplayName(p) : null;
    public string InstallLabel => D.PartOfToolId is { } p ? $"Install {_actions.DisplayName(p)}…" : "Install…";
    public bool CanInstall => D.CanInstall && !Entry.Status.Installed;
    public bool CanUpdate => D.CanUpdate && Entry.Status.Installed;
    public bool CanRepair => D.CanInstall && D.PartOfToolId is null && Entry.Status.Installed;
    public bool CanUninstall => D.CanUninstall && Entry.Status.Installed;
    public bool IsCustom => D.Id.StartsWith("custom-", StringComparison.Ordinal);

    public ICommand InstallCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand UninstallCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand PlanCommand { get; }
    public ICommand HomepageCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand CopyNameCommand { get; }
    public ICommand CopyPackageIdCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand CopyInstallCommandCommand { get; }
    public ICommand ShowInCatalogueCommand { get; }
    public ICommand ShowDownloadsCommand { get; }

    /// <summary>The standard right-click menu for a tool, grouped: main verbs, Open, Copy, then Uninstall last.</summary>
    public IReadOnlyList<MenuAction> MenuActions(bool inCatalogue)
    {
        var list = new List<MenuAction>();
        if (CanInstall) list.Add(new(InstallLabel, InstallCommand, Glyph: ""));
        if (CanUpdate) list.Add(new("Update…", UpdateCommand, Glyph: ""));
        if (CanInstall || CanUpdate) list.Add(new("View plan", PlanCommand, Glyph: ""));
        if (CanRepair) list.Add(new("Diagnose / repair…", RepairCommand, Glyph: ""));
        list.Add(new("Details", DetailsCommand, Glyph: ""));
        list.Add(new("Open homepage", HomepageCommand, Group: "Open", Glyph: ""));
        list.Add(new("Open install folder", OpenFolderCommand, Group: "Open", Glyph: ""));
        if (!inCatalogue) list.Add(new("Show in Catalogue", ShowInCatalogueCommand, Group: "Open", Glyph: ""));
        list.Add(new("Show downloads", ShowDownloadsCommand, Group: "Open", Glyph: ""));
        list.Add(new("Copy name", CopyNameCommand, Group: "Copy", Glyph: ""));
        list.Add(new("Copy winget id", CopyPackageIdCommand, Group: "Copy", Glyph: ""));
        list.Add(new("Copy executable path", CopyPathCommand, Group: "Copy", Glyph: ""));
        list.Add(new("Copy winget install command", CopyInstallCommandCommand, Group: "Copy", Glyph: ""));
        if (CanUninstall) list.Add(new("Uninstall…", UninstallCommand, Danger: true, Glyph: ""));
        return list;
    }
}
