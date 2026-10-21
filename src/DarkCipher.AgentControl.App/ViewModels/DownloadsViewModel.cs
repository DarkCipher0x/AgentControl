using System.Collections.ObjectModel;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Downloads;

namespace DarkCipher.AgentControl.App.ViewModels;

/// <summary>The unified download and installation manager: models, Hugging Face imports, agent tools and runtimes.</summary>
public sealed class DownloadsViewModel : PageViewModel, IContextActionProvider
{
    private readonly DownloadManager _downloads;
    private DownloadJob? _selected;

    public DownloadsViewModel(DownloadManager downloads, Navigator nav)
    {
        _downloads = downloads;
        downloads.SetDispatcher(Ui.OnUi);
        downloads.Changed += (_, _) => Ui.OnUi(Reload);
        CancelCommand = new RelayCommand(() => _downloads.Cancel(Selected!.Id), () => Selected is { IsFinished: false });
        PauseCommand = new RelayCommand(() => Message = _downloads.Pause(Selected!.Id).Message, () => Selected is not null && _downloads.CanPause(Selected));
        ResumeCommand = new RelayCommand(() => Message = _downloads.Resume(Selected!.Id).Message, () => Selected is { IsPaused: true });
        RetryCommand = new RelayCommand(() => Message = _downloads.Retry(Selected!.Id).Message, () => Selected is { IsFinished: true });
        RepairCommand = new RelayCommand(() => nav.Go(Selected!.Request.ProviderId == "agent-tools" ? "catalogue" : "models"), () => Selected is { IsFailed: true });
        RemoveCommand = new RelayCommand(() => Message = _downloads.Remove(Selected!.Id).Message, () => Selected is { IsFinished: true });
        DetailsCommand = new RelayCommand(ShowDetails, () => Selected is not null);
        ClearCommand = new RelayCommand(_downloads.ClearFinished);
        CopyNameCommand = new RelayCommand(() => Ui.Copy(Selected!.Request.DisplayName), () => Selected is not null);
        CopySourceCommand = new RelayCommand(() => Ui.Copy(Selected!.Request.Source.Length > 0 ? Selected.Request.Source : Selected.Request.Target), () => Selected is not null);
        CopyLogCommand = new RelayCommand(() => Ui.Copy(string.Join(Environment.NewLine, Selected!.Log)), () => Selected is not null);
    }

    public override string Title => "Downloads";
    public override string Subtitle => "One queue for model pulls, Hugging Face imports and tool installs. An item is Ready only after its post-install test passes, never just because its bytes arrived.";

    public ObservableCollection<DownloadJob> Jobs { get; } = [];
    public DownloadJob? Selected { get => _selected; set => Set(ref _selected, value); }
    public ICommand CancelCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand CopyNameCommand { get; }
    public ICommand CopySourceCommand { get; }
    public ICommand CopyLogCommand { get; }

    public IReadOnlyList<MenuAction> ContextActions(object item) => item switch
    {
        DownloadJob => [
        new("Resume", ResumeCommand, Glyph: "\uE768"),
        new("Pause", PauseCommand, Glyph: "\uE769"),
        new("Retry", RetryCommand, Glyph: "\uE72C"),
        new("Details", DetailsCommand, Glyph: "\uE946"),
        new("Fix where it came from", RepairCommand, Group: "Open", Glyph: "\uE90F"),
        new("Copy name", CopyNameCommand, Group: "Copy", Glyph: "\uE8C8"),
        new("Copy source", CopySourceCommand, Group: "Copy", Glyph: "\uE8C8"),
        new("Copy log", CopyLogCommand, Group: "Copy", Glyph: "\uE8C8"),
        new("Remove from list", RemoveCommand, Group: "List", Glyph: "\uE711"),
        new("Clear all finished", ClearCommand, Group: "List", Glyph: "\uE74D"),
        new("Cancel", CancelCommand, Danger: true, Glyph: "\uE711"),
    ],
        _ => [],
    };

    protected override Task OnActivateAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        var keep = Selected?.Id;
        Jobs.Clear();
        foreach (var j in _downloads.Jobs.OrderByDescending(j => j.QueuedAt)) Jobs.Add(j);
        Selected = Jobs.FirstOrDefault(j => j.Id == keep);
    }

    private void ShowDetails()
    {
        var j = Selected!;
        var lines = new List<string>
        {
            $"Name: {j.Request.DisplayName}",
            $"Type: {j.TypeLabel}",
            $"Source: {(j.Request.Source.Length > 0 ? j.Request.Source : j.Request.ProviderId)}",
            $"Version or tag: {j.Request.VersionOrTag ?? j.Request.Target}",
            $"Stage: {j.StageLabel}   Installation: {j.InstallState}",
            $"Verification: {j.VerificationResult ?? "not run yet"}",
            $"Final location: {j.FinalLocation ?? "—"}",
            $"Error: {j.Error ?? "none"}",
            $"Queued {j.QueuedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}; finished {(j.FinishedAt is { } f ? f.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—")}",
            j.Request.ApprovedPlanFingerprint is { } fp ? $"Approved plan: {fp}" : "",
            "",
            "Log:",
        };
        lines.AddRange(j.Log);
        Dialogs.ShowText($"Details — {j.Request.DisplayName}", string.Join(Environment.NewLine, lines.Where(l => l is not null)));
    }
}
