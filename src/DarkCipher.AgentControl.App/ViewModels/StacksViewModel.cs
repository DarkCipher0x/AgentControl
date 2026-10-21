using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Setup;
using DarkCipher.AgentControl.Application.Configuration;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Application.Verification;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Templates;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Stacks;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed class StacksViewModel : PageViewModel, IContextActionProvider
{
    private readonly StackService _stacks;
    private readonly IStackValidator _validator;
    private readonly IPermissionService _permissions;
    private readonly Navigator _nav;
    private readonly StackVerificationService _verification;
    private readonly ConfigurationTransactionService _transactions;
    private readonly IAppPaths _paths;
    private readonly IApprovalService _approval;
    private readonly DarkCipher.AgentControl.Integrations.Ollama.OllamaLauncher _launcher;
    private StackProfile? _selected;
    private string _verificationLabel = "";
    private StackReadinessReport? _report;
    private string _editName = "", _editModel = "", _editContext = "";
    private PermissionProfile? _editPermission;
    private PrivacyMode _editPrivacy;

    public IReadOnlyList<MenuAction> ContextActions(object item) => item switch
    {
        StackProfile => [
        new("Run readiness test", TestCommand, Glyph: "\uE9D9"),
        new("Run full verification", VerifyCommand, Glyph: "\uE73E"),
        new("Diagnose and repair", RepairCommand, Glyph: "\uE90F"),
        new("Start a session", LaunchCommand, Glyph: "\uE768"),
        new("Duplicate", DuplicateCommand, Group: "Manage", Glyph: "\uE8C8"),
        new("Export…", ExportCommand, Group: "Manage", Glyph: "\uEDE1"),
        new("Roll back last profile change…", RollbackCommand, Group: "Manage", Glyph: "\uE7A7"),
        new("Copy stack id", CopyIdCommand, Group: "Copy", Glyph: "\uE8C8"),
        new("Copy model name", CopyModelCommand, Group: "Copy", Glyph: "\uE8C8"),
        new("Delete…", DeleteCommand, Danger: true, Glyph: "\uE74D"),
    ],
        _ => [],
    };

    public ICommand CopyIdCommand { get; }
    public ICommand CopyModelCommand { get; }

    public StacksViewModel(StackService stacks, IStackValidator validator, IPermissionService permissions, Navigator nav, StackVerificationService verification,
        ConfigurationTransactionService transactions, IAppPaths paths, IApprovalService approval, DarkCipher.AgentControl.Integrations.Ollama.OllamaLauncher launcher)
    {
        _launcher = launcher;
        _verification = verification;
        _transactions = transactions;
        _paths = paths;
        _approval = approval;
        VerifyCommand = new AsyncCommand(() => Guard(VerifyAsync, "Verification failed"), () => Selected is not null);
        RollbackCommand = new RelayCommand(Rollback, () => Selected is not null);
        FixCommand = new RelayCommand(p => Fix(p as RepairPlan));
        _stacks = stacks;
        _validator = validator;
        _permissions = permissions;
        _nav = nav;
        stacks.Changed += (_, _) => Ui.OnUi(Reload);
        TestCommand = new AsyncCommand(() => Guard(TestAsync, "Test failed"), () => Selected is not null);
        DuplicateCommand = new RelayCommand(Duplicate, () => Selected is not null);
        DeleteCommand = new RelayCommand(Delete, () => Selected is { BuiltIn: false });
        ExportCommand = new RelayCommand(Export, () => Selected is not null);
        ImportCommand = new RelayCommand(Import);
        SaveCommand = new RelayCommand(Save, () => Selected is { BuiltIn: false });
        RepairCommand = new AsyncCommand(() => Guard(RepairAsync, "Diagnosis failed"), () => Selected is not null);
        LaunchCommand = new RelayCommand(() => nav.Go("sessions"));
        CopyIdCommand = new RelayCommand(() => Ui.Copy(Selected!.Id), () => Selected is not null);
        CopyModelCommand = new RelayCommand(() => Ui.Copy(Selected!.PrimaryModel!), () => Selected is { PrimaryModel: not null });
    }

    public override string Title => "Stacks";
    public override string Subtitle => "A stack = agent + runtime + models + permissions + privacy. Built-in templates are read-only; duplicate to customise.";

    public ObservableCollection<StackProfile> Stacks { get; } = [];
    public ObservableCollection<ReadinessCheck> Checks { get; } = [];
    public ObservableCollection<RepairPlan> Findings { get; } = [];
    public ObservableCollection<VerificationStep> VerificationSteps { get; } = [];
    public string VerificationLabel { get => _verificationLabel; private set => Set(ref _verificationLabel, value); }
    public ICommand VerifyCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand FixCommand { get; }
    public IReadOnlyList<PermissionProfile> PermissionProfiles => _permissions.Profiles;
    public IReadOnlyList<PrivacyMode> PrivacyModes { get; } = Enum.GetValues<PrivacyMode>();

    public StackProfile? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Checks.Clear();
            Findings.Clear();
            VerificationSteps.Clear();
            VerificationLabel = "";
            ReadinessText = "";
            if (value is null) return;
            EditName = value.Name;
            EditModel = value.PrimaryModel ?? "";
            EditContext = value.ContextLength?.ToString() ?? "";
            EditPermission = _permissions.Resolve(value.PermissionProfileId);
            EditPrivacy = value.Privacy;
            Raise(nameof(CanEdit));
        }
    }

    public bool CanEdit => Selected is { BuiltIn: false };
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public string EditModel { get => _editModel; set => Set(ref _editModel, value); }
    public string EditContext { get => _editContext; set => Set(ref _editContext, value); }
    public PermissionProfile? EditPermission { get => _editPermission; set => Set(ref _editPermission, value); }
    public PrivacyMode EditPrivacy { get => _editPrivacy; set => Set(ref _editPrivacy, value); }
    private string _readinessText = "";
    public string ReadinessText { get => _readinessText; private set => Set(ref _readinessText, value); }

    public ICommand TestCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand LaunchCommand { get; }

    protected override Task OnActivateAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        var keep = Selected?.Id;
        Stacks.Clear();
        foreach (var s in _stacks.All) Stacks.Add(s);
        Selected = Stacks.FirstOrDefault(s => s.Id == keep) ?? Stacks.FirstOrDefault();
    }

    private async Task TestAsync()
    {
        _report = await _validator.ValidateAsync(Selected!, CancellationToken.None);
        Checks.Clear();
        foreach (var c in _report.Checks) Checks.Add(c);
        ReadinessText = _report.Summary;
    }

    private async Task RepairAsync()
    {
        await TestAsync();
        Findings.Clear();
        foreach (var f in StackRepairService.Diagnose(Selected!, _report!)) Findings.Add(f);
        Message = Findings.Count == 0 ? "Nothing to repair: no failed check has a correction." : "Diagnosis only — nothing has been changed. Each fix opens its own planned, approved action.";
    }

    private async Task VerifyAsync()
    {
        VerificationSteps.Clear();
        VerificationLabel = "Running…";
        var progress = new Progress<string>(s => Message = s);
        var r = await _verification.VerifyAsync(Selected!.Id, _approval, progress, CancellationToken.None);
        foreach (var s in r.Steps) VerificationSteps.Add(s);
        VerificationLabel = r.Label;
        Message = r.Outcome == Domain.Verification.VerificationOutcome.SupportedAndVerified
            ? "Supported and Verified on this PC — recorded in the supported-configuration matrix."
            : $"{r.Label}. The exact result was recorded; see the steps for what is missing.";
    }

    private void Rollback()
    {
        var target = Path.Combine(_paths.AgentProfilesDirectory, Selected!.Id, "config.toml");
        if (!Dialogs.Confirm("Roll back last change", $"Restore the previous version of this stack's agent profile?", [target], "Roll back")) return;
        Message = _transactions.RollBackLast(target).Message;
    }

    private void Fix(RepairPlan? plan)
    {
        if (plan is null) return;
        switch (plan.Action)
        {
            case CorrectiveAction.InstallAgent or CorrectiveAction.UpdateAgent or CorrectiveAction.InstallRuntime: _nav.Go("catalogue"); break;
            case CorrectiveAction.PullModel or CorrectiveAction.RunCompatibilityTest or CorrectiveAction.RepairGateway: _nav.Go("models"); break;
            case CorrectiveAction.StartRuntime: _ = Guard(StartRuntimeAsync, "Start failed"); break;
            case CorrectiveAction.ChangePrivacyMode or CorrectiveAction.ApproveNetworkDestination: _nav.Go("security"); break;
            case CorrectiveAction.RollBackConfiguration: Rollback(); break;
            default: Message = "Use the Edit card above: " + plan.ProposedCorrection; break;
        }
    }

    /// <summary>Starts the installed Ollama with its own app (it keeps running after this app closes), then re-checks.</summary>
    private async Task StartRuntimeAsync()
    {
        Message = "Starting Ollama…";
        var r = await _launcher.EnsureRunningAsync(CancellationToken.None);
        if (!r.Success) { Message = r.Message; return; }
        await TestAsync();
        Message = r.Message + " Readiness re-checked.";
    }

    private void Duplicate()
    {
        var copy = _stacks.Duplicate(Selected!.Id);
        Message = $"Created '{copy.Name}'.";
        Selected = Stacks.FirstOrDefault(s => s.Id == copy.Id);
    }

    private void Delete()
    {
        if (!Dialogs.Confirm("Delete stack", $"Delete '{Selected!.Name}'? Projects and models are not affected.", null, "Delete", danger: true)) return;
        Message = _stacks.Delete(Selected.Id).Message;
    }

    private void Save()
    {
        int? ctx = int.TryParse(EditContext, out var c) ? c : null;
        var updated = Selected! with
        {
            Name = EditName.Trim(),
            PrimaryModel = string.IsNullOrWhiteSpace(EditModel) ? null : EditModel.Trim(),
            ContextLength = ctx,
            PermissionProfileId = EditPermission?.Id ?? BuiltInPermissionProfiles.DefaultId,
            Privacy = EditPrivacy,
        };
        var r = _stacks.Save(updated);
        Message = r.Message;
    }

    private void Export()
    {
        var json = _stacks.Export(Selected!.Id);
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = Selected.Id + ".stack.json", Filter = "Stack profile|*.stack.json;*.json" };
        if (Dialogs.Headless || dlg.ShowDialog() != true) { Ui.Copy(json); Message = "Copied to the clipboard (stack profiles never contain secrets)."; return; }
        File.WriteAllText(dlg.FileName, json);
        Message = "Exported (stack profiles never contain secrets).";
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Stack profile|*.json" };
        if (Dialogs.Headless || dlg.ShowDialog() != true) return;
        if (new FileInfo(dlg.FileName).Length > 64 * 1024) { Message = "That file is too large to be a stack profile."; return; }
        var r = _stacks.Import(File.ReadAllText(dlg.FileName));
        Message = r.Message;
    }
}
