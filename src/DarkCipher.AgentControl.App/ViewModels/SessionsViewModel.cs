using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.Application.Sessions;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Sessions;
using DarkCipher.AgentControl.Domain.Stacks;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed class SessionsViewModel : PageViewModel, DarkCipher.AgentControl.App.Shell.IContextActionProvider
{
    private readonly SessionService _sessions;
    private readonly StackService _stacks;
    private readonly ProjectService _projects;
    private readonly IApprovalService _approval;
    private StackProfile? _stack;
    private ProjectEntry? _project;
    private SessionInfo? _selected;
    private string _preview = "Choose a stack and a project to see exactly what will run.";

    public SessionsViewModel(SessionService sessions, StackService stacks, ProjectService projects, IApprovalService approval)
    {
        _sessions = sessions;
        _stacks = stacks;
        _projects = projects;
        _approval = approval;
        sessions.Changed += (_, _) => Ui.OnUi(ReloadSessions);
        StartCommand = new AsyncCommand(() => Guard(StartAsync, "Session did not start"), () => Stack is not null && Project is not null);
        StopCommand = new RelayCommand(Stop, () => Selected is { State: SessionState.Running });
        RestartCommand = new AsyncCommand(() => Guard(RestartAsync, "Restart failed"), () => Selected is not null);
        CopyCommand = new RelayCommand(() => { if (Selected is not null) { Ui.Copy(Selected.CommandPreview); Message = "Command copied (secrets are never included)."; } }, () => Selected is not null);
    }

    public override string Title => "Sessions";
    public override string Subtitle => "Agent sessions this app started. Each opens in its own terminal window; only sessions started here can be stopped here.";

    public ObservableCollection<StackProfile> Stacks { get; } = [];
    public ObservableCollection<ProjectEntry> Projects { get; } = [];
    public ObservableCollection<SessionInfo> Sessions { get; } = [];

    public StackProfile? Stack { get => _stack; set { if (Set(ref _stack, value)) _ = Guard(PreviewAsync, "Preview failed"); } }
    public ProjectEntry? Project { get => _project; set { if (Set(ref _project, value)) _ = Guard(PreviewAsync, "Preview failed"); } }
    public SessionInfo? Selected { get => _selected; set => Set(ref _selected, value); }

    public IReadOnlyList<DarkCipher.AgentControl.App.Shell.MenuAction> ContextActions(object item) => item switch
    {
        SessionInfo => [
        new("Restart", RestartCommand, Glyph: "\uE72C"),
        new("Copy command line", CopyCommand, Group: "Copy", Glyph: "\uE8C8"),
        new("Stop", StopCommand, Danger: true, Glyph: "\uE71A"),
    ],
        _ => [],
    };
    public string Preview { get => _preview; private set => Set(ref _preview, value); }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand CopyCommand { get; }

    protected override Task OnActivateAsync()
    {
        Stacks.Clear();
        foreach (var s in _stacks.All) Stacks.Add(s);
        Projects.Clear();
        foreach (var p in _projects.All) Projects.Add(p);
        ReloadSessions();
        return Task.CompletedTask;
    }

    private void ReloadSessions()
    {
        var keep = Selected?.Id;
        Sessions.Clear();
        foreach (var s in _sessions.All) Sessions.Add(s);
        Selected = Sessions.FirstOrDefault(s => s.Id == keep);
    }

    private async Task PreviewAsync()
    {
        if (Stack is null || Project is null) return;
        var r = await _sessions.PreviewAsync(Stack.Id, Project.Id, CancellationToken.None);
        if (!r.Success || r.Value is null) { Preview = "Cannot start: " + r.Message; return; }
        var p = r.Value;
        var sb = new StringBuilder();
        sb.AppendLine("Backend:      " + p.Plan.BackendDescription);
        sb.AppendLine("Maturity:     " + p.Plan.Maturity);
        sb.AppendLine("Folder:       " + p.ProjectDecision.CanonicalPath + (p.ProjectDecision.Kind == PathDecisionKind.RequiresApproval ? "  (approval required)" : ""));
        sb.AppendLine("Permissions:  " + p.EffectiveProfile.Name + (p.PermissionNote is null ? "" : "  — " + p.PermissionNote));
        foreach (var f in p.Plan.FileChanges) sb.AppendLine("Writes:       " + f.Path + "  (" + f.Reason + ")");
        foreach (var w in p.Plan.Warnings) sb.AppendLine("⚠ " + w);
        sb.AppendLine();
        sb.AppendLine(p.CommandPreview);
        Preview = sb.ToString();
    }

    private async Task StartAsync()
    {
        var r = await _sessions.StartAsync(Stack!.Id, Project!.Id, _approval, CancellationToken.None);
        Message = r.Message;
    }

    private void Stop() => Message = _sessions.Stop(Selected!.Id).Message;

    private async Task RestartAsync()
    {
        var r = await _sessions.RestartAsync(Selected!.Id, _approval, CancellationToken.None);
        Message = r.Message;
    }
}
