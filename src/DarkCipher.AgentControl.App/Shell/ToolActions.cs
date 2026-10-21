using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Tools;

namespace DarkCipher.AgentControl.App.Shell;

/// <summary>
/// One place for what can be done with a catalogue tool, so the Catalogue, Agents, Services and Quick Setup pages (and
/// every right-click menu) behave identically: plan → approval → queue for install/update/uninstall, diagnose-first
/// repair, and helpers to open the homepage or install folder. When a tool job finishes in the queue, the catalogue is
/// detected again so every page shows the new state without a manual refresh.
/// </summary>
public sealed class ToolActions
{
    private readonly AgentToolCatalogueService _catalogue;
    private readonly AgentToolInstallationService _install;
    private readonly AgentToolRepairService _repair;
    private readonly IApprovalService _approval;
    private readonly IProcessExecutor _processes;
    private readonly DownloadManager _downloads;
    private readonly Navigator _nav;
    private readonly HashSet<string> _seenFinished = [];

    public ToolActions(AgentToolCatalogueService catalogue, AgentToolInstallationService install, AgentToolRepairService repair, IApprovalService approval,
        IProcessExecutor processes, DownloadManager downloads, Navigator nav)
    {
        _catalogue = catalogue; _install = install; _repair = repair; _approval = approval; _processes = processes; _downloads = downloads; _nav = nav;
        lock (_seenFinished) foreach (var j in downloads.Jobs.Where(j => j.IsFinished)) _seenFinished.Add(j.Id);
        downloads.Changed += (_, _) => OnQueueChanged();
    }

    /// <summary>Short results for the page's status line ("queued", "not approved", …).</summary>
    public event EventHandler<string>? Status;

    public AgentToolCatalogueService Catalogue => _catalogue;

    private void Report(string message) => Ui.OnUi(() => Status?.Invoke(this, message));

    private void OnQueueChanged()
    {
        var finished = false;
        lock (_seenFinished)
            foreach (var j in _downloads.Jobs.Where(j => j.IsFinished && j.Request.ProviderId == AgentToolQueueProvider.Id_))
                finished |= _seenFinished.Add(j.Id);
        if (finished) _ = RefreshSafeAsync();
    }

    private async Task RefreshSafeAsync()
    {
        try { await _catalogue.RefreshAsync(false, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Report("Could not re-detect tools: " + ex.Message); }
    }

    public IAgentToolProvider? Find(string id) => _catalogue.Find(id);

    public string DisplayName(string id) => Find(id)?.Descriptor.DisplayName ?? id;

    public async Task RunAsync(string toolId, InstallOperation op)
    {
        var plan = await _install.PlanAsync(toolId, op, CancellationToken.None);
        if (!plan.Success || plan.Value is null) { Report(plan.Message); return; }
        var r = await _install.SubmitAsync(plan.Value, _approval, CancellationToken.None);
        Report(r.Success ? $"{op} of {plan.Value.DisplayName} queued: progress, the post-install test and the result are in Downloads." : r.Message);
    }

    public async Task ShowPlanAsync(string toolId, InstallOperation op)
    {
        var plan = await _install.PlanAsync(toolId, op, CancellationToken.None);
        if (!plan.Success || plan.Value is null) { Report(plan.Message); return; }
        Dialogs.ShowText($"{op} plan — {plan.Value.DisplayName}", string.Join(Environment.NewLine, plan.Value.ApprovalDetails()));
    }

    public async Task RepairAsync(string toolId)
    {
        var d = await _repair.DiagnoseAsync(toolId, CancellationToken.None);
        if (!d.NeedsRepair || d.Plan is null)
        {
            Dialogs.Info($"Diagnosis — {DisplayName(toolId)}", d.Issue + Environment.NewLine + Environment.NewLine + "Evidence: " + d.Evidence);
            Report(d.Issue);
            return;
        }
        var r = await _install.SubmitAsync(d.Plan, _approval, CancellationToken.None);
        Report(r.Success ? "Repair queued (the same version is reinstalled over the broken one)." : r.Message);
    }

    /// <summary>Opens a reviewed https homepage in the default browser (explorer.exe through the secure executor).</summary>
    public async Task OpenHomepageAsync(AgentToolDescriptor d)
    {
        var url = d.Source?.Homepage;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) { Report("No https homepage is recorded for this tool."); return; }
        await ExplorerAsync(u.AbsoluteUri, $"Open homepage of {d.DisplayName}");
    }

    public async Task OpenFolderAsync(string? path, string what)
    {
        var folder = path is null ? null : File.Exists(path) ? Path.GetDirectoryName(path) : Directory.Exists(path) ? path : null;
        if (folder is null) { Report("The folder is not known or no longer exists."); return; }
        await ExplorerAsync(folder, "Open folder of " + what);
    }

    private async Task ExplorerAsync(string argument, string purpose)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        // explorer.exe returns 1 even on success; only a block is worth reporting.
        var r = await _processes.RunAsync(new ProcessSpec { Executable = explorer, Arguments = [argument], Timeout = TimeSpan.FromSeconds(10), Purpose = purpose }, CancellationToken.None);
        if (r.Outcome == ProcessOutcome.Blocked) Report(r.Message);
    }

    public void ShowDetails(ToolEntry e)
    {
        var d = e.Descriptor;
        var lines = new List<string?>
        {
            $"{d.DisplayName} — {d.Publisher}",
            d.Description,
            "",
            $"Kind: {d.Kind}{(d.CatalogueOnly ? " (catalogue only: install, update, detect and remove)" : "")}",
            $"State: {e.Status.State} · health {e.Status.Health} · {e.Status.Summary}",
            $"Installed version: {e.Status.Version ?? "—"} · available: {e.AvailableVersion ?? "not checked"} · verified by this app: {d.LatestVerifiedVersion ?? "not yet verified"}",
            $"Executable: {e.Status.ExecutablePath ?? "—"}",
            $"Source: {d.Source?.Display ?? d.InstallMethod}",
            $"Licence: {d.Source?.Licence ?? "—"}",
            $"Homepage: {d.Source?.Homepage ?? "—"}",
            $"Install method: {d.InstallMethod}",
            $"Authentication: {d.Authentication}",
            $"Protocols: {(d.SupportedProtocols.Count == 0 ? "—" : string.Join(", ", d.SupportedProtocols))}",
            d.LocalPort is { } port ? $"Local port: 127.0.0.1:{port}" : null,
            d.PartOfToolId is { } parent ? $"Part of: {DisplayName(parent)}" : null,
            $"Trust: {e.TrustLabel}",
        };
        lines.AddRange(d.AffectedLocations.Select(l => "Files: " + l));
        lines.AddRange(d.EnvironmentChanges.Select(l => "Environment: " + l));
        lines.AddRange(d.KnownLimitations.Select(l => "Limitation: " + l));
        Dialogs.ShowText(d.DisplayName, string.Join(Environment.NewLine, lines.OfType<string>()));
    }

    public void GoCatalogue() => _nav.Go("catalogue");
    public void GoDownloads() => _nav.Go("downloads");
}
