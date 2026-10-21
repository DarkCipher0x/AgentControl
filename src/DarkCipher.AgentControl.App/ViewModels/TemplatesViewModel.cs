using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows.Media;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Sessions;
using DarkCipher.AgentControl.Application.Templates;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed record TemplateRow(TemplateDefinition Template, TemplateCompatibility Result)
{
    public string Name => Template.DisplayName;
    public string Category => Template.Category.ToString();
    public string Version => "v" + Template.Version;
    public string Origin => Template.Origin.ToString();
    public string Summary => Result.Summary;
    public bool CanLaunch => Result.CanLaunch;
    public Brush Brush => !Result.CanLaunch ? StatusBrushes.Bad : Result.HasWarnings ? StatusBrushes.Warn : StatusBrushes.Good;
}

public sealed record CheckRow(CompatibilityCheck Check)
{
    public string Name => Check.Name;
    public string Detail => Check.Detail;
    public string Outcome => Check.Outcome switch { CheckOutcome.Passed => "Passed", CheckOutcome.Warning => "Warning", CheckOutcome.Failed => "Failed", _ => "n/a" };
    public Brush Brush => Check.Outcome switch { CheckOutcome.Passed => StatusBrushes.Good, CheckOutcome.Warning => StatusBrushes.Warn, CheckOutcome.Failed => StatusBrushes.Bad, _ => StatusBrushes.Muted };
    public bool HasFix => Check.Fix != CorrectiveAction.None && Check.Outcome != CheckOutcome.Passed;
    public string FixLabel => Check.Fix switch
    {
        CorrectiveAction.InstallAgent => "Install agent", CorrectiveAction.UpdateAgent => "Update agent", CorrectiveAction.InstallRuntime => "Install runtime",
        CorrectiveAction.StartRuntime => "Start runtime", CorrectiveAction.PullModel => "Pull model", CorrectiveAction.ChooseCompatibleModel => "Choose compatible model",
        CorrectiveAction.IncreaseStorage => "Increase storage", CorrectiveAction.ReduceContext => "Reduce context", CorrectiveAction.ConfigureAuthentication => "Configure authentication",
        CorrectiveAction.RepairGateway => "Repair gateway", CorrectiveAction.ChangePermissionProfile => "Change permission profile", CorrectiveAction.ChangePrivacyMode => "Change privacy mode",
        CorrectiveAction.RunCompatibilityTest => "Run compatibility test", CorrectiveAction.ChooseProject => "Choose a project", CorrectiveAction.ValidateCustomAgent => "Validate custom tool",
        CorrectiveAction.ApproveNetworkDestination => "Approve endpoint", _ => Check.Fix.ToString(),
    };
}

public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Templates turn installed components into a working stack. Pick an agent (and optionally model, project, permissions,
/// context, reasoning); the list shows only templates that fit, each with every check and a corrective action. Launch is
/// enabled only when no check failed, and goes through the normal session service and its approvals.
/// </summary>
public sealed class TemplatesViewModel : PageViewModel, IContextActionProvider
{
    private readonly TemplateEngine _engine;
    private readonly IEnumerable<IAgentProvider> _agents;
    private readonly IModelRuntime _runtime;
    private readonly ProjectService _projects;
    private readonly IPermissionService _permissions;
    private readonly IApprovalService _approval;
    private readonly Navigator _nav;
    private readonly DarkCipher.AgentControl.Integrations.Ollama.OllamaLauncher _launcher;
    private Choice? _agent, _mode, _project, _permission, _context, _reasoning;
    private string _model = "";
    private TemplateRow? _selected;
    private string _output = "";
    private bool _loading;

    public TemplatesViewModel(TemplateEngine engine, IEnumerable<IAgentProvider> agents, IModelRuntime runtime, ProjectService projects, IPermissionService permissions,
        IApprovalService approval, Navigator nav, DarkCipher.AgentControl.Integrations.Ollama.OllamaLauncher launcher)
    {
        _launcher = launcher;
        _engine = engine; _agents = agents; _runtime = runtime; _projects = projects; _permissions = permissions; _approval = approval; _nav = nav;
        RefreshCommand = new AsyncCommand(() => Guard(FilterAsync, "Refresh failed"));
        PreviewCommand = new AsyncCommand(() => Guard(PreviewAsync, "Preview failed"), () => Selected is not null);
        DryRunCommand = new AsyncCommand(() => Guard(DryRunAsync, "Dry run failed"), () => Selected is not null);
        CreateStackCommand = new AsyncCommand(() => Guard(CreateAsync, "Create failed"), () => Selected is not null);
        LaunchCommand = new AsyncCommand(() => Guard(LaunchAsync, "Launch failed"), () => Selected is { CanLaunch: true } && _project is not null);
        DuplicateCommand = new RelayCommand(Duplicate, () => Selected is not null);
        DeleteCommand = new RelayCommand(Delete, () => Selected is not null && !engine.Catalogue.IsReadOnly(Selected.Template.Id));
        ResetCommand = new RelayCommand(Reset, () => Selected is { Template.DerivedFrom: not null });
        ExportCommand = new RelayCommand(Export, () => Selected is not null);
        ImportCommand = new RelayCommand(Import);
        FixCommand = new RelayCommand(p => Fix(p as CheckRow));
        engine.Catalogue.Changed += (_, _) => Ui.OnUi(() => _ = Guard(FilterAsync, "Refresh failed"));
    }

    public override string Title => "Templates";
    public override string Subtitle => "Versioned, validated templates connect an agent, a runtime, a model and a project into a stack. Only templates that fit your selection are shown.";

    public ObservableCollection<Choice> Agents { get; } = [];
    public ObservableCollection<Choice> Modes { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<Choice> Projects { get; } = [];
    public ObservableCollection<Choice> PermissionProfiles { get; } = [];
    public IReadOnlyList<Choice> Contexts { get; } = [new("", "Template default"), new("8192", "8K"), new("16384", "16K"), new("32768", "32K"), new("65536", "64K"), new("131072", "128K")];
    public IReadOnlyList<Choice> ReasoningLevels { get; } = [new("", "Template default"), new("low", "Low"), new("medium", "Medium"), new("high", "High")];
    public ObservableCollection<TemplateRow> Templates { get; } = [];
    public ObservableCollection<CheckRow> Checks { get; } = [];

    public Choice? Agent { get => _agent; set { if (Set(ref _agent, value)) { FillModes(); Refilter(); } } }
    public Choice? Mode { get => _mode; set { if (Set(ref _mode, value)) Refilter(); } }
    public string Model { get => _model; set { if (Set(ref _model, value)) Refilter(); } }
    public Choice? Project { get => _project; set { if (Set(ref _project, value)) Refilter(); } }
    public Choice? Permission { get => _permission; set { if (Set(ref _permission, value)) Refilter(); } }
    public Choice? Context { get => _context; set { if (Set(ref _context, value)) Refilter(); } }
    public Choice? Reasoning { get => _reasoning; set { if (Set(ref _reasoning, value)) Refilter(); } }
    public TemplateRow? Selected { get => _selected; set { if (Set(ref _selected, value)) ShowChecks(); } }
    public string Output { get => _output; private set => Set(ref _output, value); }
    public string LaunchBlockedReason => Selected is null ? "Select a template." : _project is null ? "Choose a project to launch."
        : Selected.CanLaunch ? "" : "Launch is disabled: " + string.Join("; ", Selected.Result.Checks.Where(c => c.Outcome == CheckOutcome.Failed).Select(c => $"{c.Name} — {c.Detail}"));

    public ICommand RefreshCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand DryRunCommand { get; }
    public ICommand CreateStackCommand { get; }
    public ICommand LaunchCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand FixCommand { get; }
    public ICommand CopyIdCommand => _copyId ??= new RelayCommand(() => Ui.Copy(Selected!.Template.Id), () => Selected is not null);
    private ICommand? _copyId;

    public IReadOnlyList<MenuAction> ContextActions(object item) => item switch
    {
        TemplateRow =>
        [
            new("Preview configuration", PreviewCommand, Glyph: "\uE7B3"),
            new("Dry run", DryRunCommand, Glyph: "\uE9D9"),
            new("Create stack", CreateStackCommand, Glyph: "\uE710"),
            new("Launch in project", LaunchCommand, Glyph: "\uE768"),
            new("Duplicate to customise", DuplicateCommand, Group: "Manage", Glyph: "\uE8C8"),
            new("Reset to the original", ResetCommand, Group: "Manage", Glyph: "\uE7A7"),
            new("Export…", ExportCommand, Group: "Manage", Glyph: "\uEDE1"),
            new("Copy template id", CopyIdCommand, Group: "Copy", Glyph: "\uE8C8"),
            new("Delete…", DeleteCommand, Danger: true, Glyph: "\uE74D"),
        ],
        CheckRow c when c.Check.Fix != Domain.Templates.CorrectiveAction.None => [new("Fix: " + c.FixLabel, FixCommand, c, Glyph: "\uE90F")],
        _ => [],
    };

    protected override async Task OnActivateAsync()
    {
        _loading = true;
        try
        {
            if (Agents.Count == 0)
                foreach (var a in _agents) Agents.Add(new Choice(a.Id, a.DisplayName));
            PermissionProfiles.Clear();
            PermissionProfiles.Add(new Choice("", "Template default"));
            foreach (var p in _permissions.Profiles) PermissionProfiles.Add(new Choice(p.Id, p.Name));
            Projects.Clear();
            foreach (var p in _projects.All) Projects.Add(new Choice(p.Id, p.Name));
            Models.Clear();
            try { foreach (var m in await _runtime.ListModelsAsync(CancellationToken.None)) Models.Add(m.Name); }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or Domain.Network.NetworkBlockedException) { }
            _agent ??= Agents.FirstOrDefault(a => a.Value == "codex") ?? Agents.FirstOrDefault();
            _permission ??= PermissionProfiles[0];
            _context ??= Contexts[0];
            _reasoning ??= ReasoningLevels[0];
            FillModes();
            Raise(nameof(Agent)); Raise(nameof(Permission)); Raise(nameof(Context)); Raise(nameof(Reasoning));
        }
        finally { _loading = false; }
        await FilterAsync();
    }

    private void FillModes()
    {
        Modes.Clear();
        Modes.Add(new Choice("", "Any mode"));
        var agent = _agents.FirstOrDefault(a => a.Id == _agent?.Value);
        if (agent is not null) foreach (var m in agent.Modes) Modes.Add(new Choice(m.Mode.ToString(), m.Label));
        _mode = Modes[0];
        Raise(nameof(Mode));
    }

    private void Refilter()
    {
        if (!_loading) _ = Guard(FilterAsync, "Filter failed");
    }

    private TemplateSelection Selection(TemplateDefinition? t = null) => new()
    {
        AgentId = _agent?.Value ?? "codex",
        AgentMode = Enum.TryParse<AgentMode>(_mode?.Value, out var m) ? m : null,
        Model = string.IsNullOrWhiteSpace(_model) ? null : _model.Trim(),
        ProjectId = string.IsNullOrEmpty(_project?.Value) ? null : _project!.Value,
        PermissionProfileId = string.IsNullOrEmpty(_permission?.Value) ? null : _permission!.Value,
        ContextLength = int.TryParse(_context?.Value, out var c) ? c : null,
        Reasoning = string.IsNullOrEmpty(_reasoning?.Value) ? null : _reasoning!.Value,
    };

    private async Task FilterAsync()
    {
        var keep = Selected?.Template.Id;
        var results = await _engine.CompatibleAsync(Selection(), CancellationToken.None);
        Templates.Clear();
        foreach (var (t, r) in results) Templates.Add(new TemplateRow(t, r));
        Selected = Templates.FirstOrDefault(x => x.Template.Id == keep) ?? Templates.FirstOrDefault();
        Message = Templates.Count == 0 ? "No template supports this selection. Try another agent or mode." :
            $"{Templates.Count} template(s) fit; {Templates.Count(t => t.CanLaunch)} can launch now.";
    }

    private void ShowChecks()
    {
        Checks.Clear();
        if (Selected is null) return;
        foreach (var c in Selected.Result.Checks) Checks.Add(new CheckRow(c));
        Output = Selected.Template.Description + Environment.NewLine + Environment.NewLine +
                 string.Join(Environment.NewLine, Selected.Template.KnownLimitations.Select(l => "Limitation: " + l));
        Raise(nameof(LaunchBlockedReason));
    }

    private async Task PreviewAsync()
    {
        var r = await _engine.PreviewAsync(Selected!.Template.Id, Selection(), CancellationToken.None);
        if (!r.Success || r.Value is null) { Output = r.Message; return; }
        var pretty = JsonNode.Parse(r.Value.CanonicalJson)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Output = $"Configuration fingerprint {r.Value.Fingerprint} (the same inputs always give the same fingerprint).{Environment.NewLine}" +
                 string.Join(Environment.NewLine, r.Value.Warnings.Select(w => "⚠ " + w)) + Environment.NewLine + pretty;
    }

    private async Task DryRunAsync()
    {
        var d = await _engine.DryRunAsync(Selected!.Template.Id, Selection(), CancellationToken.None);
        var lines = new List<string> { $"Dry run — nothing was written or started.", d.Compatibility.Summary };
        lines.AddRange(d.Compatibility.Checks.Select(c => $"  [{c.Outcome}] {c.Name}: {c.Detail}"));
        if (d.Configuration is { } g) lines.Add($"Generated stack '{g.Stack.Name}' (configuration {g.Fingerprint}).");
        if (d.Launch is { } l) lines.Add("Launch command: " + l.CommandPreview);
        lines.AddRange(d.Notes);
        Output = string.Join(Environment.NewLine, lines);
    }

    private async Task CreateAsync()
    {
        var r = await _engine.CreateStackAsync(Selected!.Template.Id, Selection(), CancellationToken.None);
        Message = r.Message;
    }

    private async Task LaunchAsync()
    {
        var r = await _engine.LaunchAsync(Selected!.Template.Id, Selection(), _approval, CancellationToken.None);
        Message = r.Message;
        if (r.Success) _nav.Go("sessions");
    }

    private void Duplicate()
    {
        var r = _engine.Catalogue.Duplicate(Selected!.Template.Id);
        Message = r.Success ? $"Created '{r.Value!.DisplayName}'. Edit it by exporting, changing and importing, or use it as is." : r.Message;
    }

    private void Delete()
    {
        if (!Dialogs.Confirm("Delete template", $"Delete '{Selected!.Name}'? Stacks created from it are kept.", null, "Delete", danger: true)) return;
        Message = _engine.Catalogue.Delete(Selected.Template.Id).Message;
    }

    private void Reset() => Message = _engine.Catalogue.ResetToDefault(Selected!.Template.Id).Message;

    private void Export()
    {
        var r = _engine.Catalogue.Export(Selected!.Template.Id);
        if (!r.Success || r.Value is null) { Message = r.Message; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Selected.Template.Id + ".template.json", Filter = "Template|*.template.json;*.json" };
        if (Dialogs.Headless || dlg.ShowDialog() != true) { Ui.Copy(r.Value); Message = "Copied to the clipboard (templates never contain secrets)."; return; }
        File.WriteAllText(dlg.FileName, r.Value);
        Message = "Exported.";
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Template|*.json" };
        if (Dialogs.Headless || dlg.ShowDialog() != true) return;
        if (new FileInfo(dlg.FileName).Length > 64 * 1024) { Message = "That file is too large to be a template."; return; }
        var preview = _engine.Catalogue.PreviewImport(File.ReadAllText(dlg.FileName));
        var details = preview.Notes.Concat(preview.Issues.Select(i => $"Problem — {i.Field}: {i.Problem}")).ToList();
        if (!preview.Valid) { Dialogs.Show("Import refused", preview.Message, details, ["Close"], "Bad"); return; }
        if (!Dialogs.Confirm("Import template", $"Import '{preview.Template!.DisplayName}' v{preview.Template.Version}?", details, "Import")) return;
        Message = _engine.Catalogue.ApplyImport(preview).Message;
    }

    /// <summary>Starts the installed Ollama with its own app (it keeps running after this app closes), then re-checks.</summary>
    private async Task StartRuntimeAsync()
    {
        Message = "Starting Ollama…";
        var r = await _launcher.EnsureRunningAsync(CancellationToken.None);
        if (!r.Success) { Message = r.Message; return; }
        await FilterAsync();
        Message = r.Message + " Compatibility re-checked.";
    }

    private void Fix(CheckRow? row)
    {
        switch (row?.Check.Fix)
        {
            case CorrectiveAction.InstallAgent or CorrectiveAction.UpdateAgent or CorrectiveAction.InstallRuntime or CorrectiveAction.ValidateCustomAgent: _nav.Go("catalogue"); break;
            case CorrectiveAction.PullModel or CorrectiveAction.RunCompatibilityTest or CorrectiveAction.RepairGateway: _nav.Go("models"); break;
            case CorrectiveAction.StartRuntime: _ = Guard(StartRuntimeAsync, "Start failed"); break;
            case CorrectiveAction.ChangePrivacyMode or CorrectiveAction.ApproveNetworkDestination: _nav.Go("security"); break;
            case CorrectiveAction.ChooseProject: _nav.Go("projects"); break;
            case CorrectiveAction.ChooseCompatibleModel: Message = "Pick a model from the list that passed the compatibility suite."; break;
            case CorrectiveAction.ReduceContext: Message = "Choose a smaller context above."; break;
            case CorrectiveAction.ChangePermissionProfile: Message = "Choose another permission profile above."; break;
            case CorrectiveAction.ConfigureAuthentication: Message = "Start the agent once in a session; it asks for its own sign-in."; break;
        }
    }
}
