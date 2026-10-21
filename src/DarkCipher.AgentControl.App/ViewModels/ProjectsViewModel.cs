using System.Collections.ObjectModel;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Sessions;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Sessions;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed record ProjectRow(ProjectEntry Project, string Access)
{
    public string Name => Project.Name;
    public string Path => Project.Path;
}

public sealed class ProjectsViewModel : PageViewModel, IContextActionProvider
{
    private readonly ProjectService _projects;
    private ProjectRow? _selected;

    public ProjectsViewModel(ProjectService projects, Navigator nav, ToolActions actions)
    {
        _projects = projects;
        OpenFolderCommand = new AsyncCommand(() => actions.OpenFolderAsync(Selected?.Path, Selected?.Name ?? "project"), () => Selected is not null);
        CopyPathCommand = new RelayCommand(() => Ui.Copy(Selected!.Path), () => Selected is not null);
        CopyNameCommand = new RelayCommand(() => Ui.Copy(Selected!.Name), () => Selected is not null);
        actions.Status += (_, m) => Message = m;
        projects.Changed += (_, _) => Ui.OnUi(Reload);
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => Selected is not null);
        SessionCommand = new RelayCommand(() => nav.Go("sessions"), () => Selected is not null);
    }

    public override string Title => "Projects";
    public override string Subtitle => "Folders you explicitly allow agents to work in. Protected folders are checked again at every launch.";

    public ObservableCollection<ProjectRow> Projects { get; } = [];
    public ProjectRow? Selected { get => _selected; set => Set(ref _selected, value); }

    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand SessionCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand CopyPathCommand { get; }
    public ICommand CopyNameCommand { get; }

    public IReadOnlyList<MenuAction> ContextActions(object item) => item switch
    {
        ProjectRow => [
        new("Start a session here", SessionCommand, Glyph: "\uE768"),
        new("Open folder", OpenFolderCommand, Glyph: "\uE838"),
        new("Copy path", CopyPathCommand, Group: "Copy", Glyph: "\uE8C8"),
        new("Copy name", CopyNameCommand, Group: "Copy", Glyph: "\uE8C8"),
        new("Remove from list…", RemoveCommand, Danger: true, Glyph: "\uE74D"),
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
        Projects.Clear();
        foreach (var p in _projects.All)
        {
            var d = _projects.Check(p);
            var access = d.Kind switch
            {
                PathDecisionKind.Allow => "Allowed",
                PathDecisionKind.RequiresApproval => "Needs approval: " + d.Reason,
                _ => "Blocked: " + d.Reason,
            };
            Projects.Add(new ProjectRow(p, access));
        }
    }

    private void Add()
    {
        if (Dialogs.Headless) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a project folder for agents" };
        if (dlg.ShowDialog() != true) return;
        var r = _projects.Add(dlg.FolderName);
        Message = r.Message;
    }

    private void Remove()
    {
        if (!Dialogs.Confirm("Remove project", $"Remove '{Selected!.Name}' from this list? The folder and its files are NOT touched.", null, "Remove")) return;
        _projects.Remove(Selected.Project.Id);
        Message = "Removed from the list.";
    }
}
