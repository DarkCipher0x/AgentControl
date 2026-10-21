using System.Collections.ObjectModel;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Policy;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed class SecurityViewModel : PageViewModel
{
    private readonly SecurityPolicyService _policy;
    private readonly IProtectedPathService _paths;
    private readonly ICredentialStore _credentials;
    private readonly IAuditLogger _audit;
    private ProtectedPathEntry? _selectedPath;
    private CredentialReference? _selectedCredential;
    private string? _selectedEndpoint;
    private string _auditStatus = "";
    private bool _ruleReadOnly = true, _ruleBlockAll, _ruleAskWrites, _ruleAskDelete = true, _ruleNoExec, _ruleNoAddOn = true;

    public SecurityViewModel(SecurityPolicyService policy, IProtectedPathService paths, ICredentialStore credentials, IAuditLogger audit)
    {
        _policy = policy;
        _paths = paths;
        _credentials = credentials;
        _audit = audit;
        policy.Changed += (_, _) => Ui.OnUi(RaisePolicy);
        AddPathCommand = new RelayCommand(AddPath);
        RemovePathCommand = new RelayCommand(RemovePath, () => SelectedPath is { BuiltIn: false });
        AddCredentialCommand = new RelayCommand(AddCredential);
        RemoveCredentialCommand = new RelayCommand(RemoveCredential, () => SelectedCredential is not null);
        AddEndpointCommand = new RelayCommand(AddEndpoint);
        RevokeEndpointCommand = new RelayCommand(() => { Message = _policy.RevokeEndpoint(SelectedEndpoint!).Message; Reload(); }, () => SelectedEndpoint is not null);
        VerifyAuditCommand = new RelayCommand(VerifyAudit);
        RefreshAuditCommand = new RelayCommand(Reload);
    }

    public override string Title => "Security";
    public override string Subtitle => "Central policy, protected folders, credentials, network approvals and the tamper-evident audit log.";

    // ---- Policy ----
    public IReadOnlyList<PrivacyMode> PrivacyModes { get; } = Enum.GetValues<PrivacyMode>();

    public PrivacyMode Privacy
    {
        get => _policy.Current.Privacy;
        set
        {
            if (value == _policy.Current.Privacy) return;
            if (value > _policy.Current.Privacy && !Dialogs.Confirm("Relax privacy mode?", $"Switch from {_policy.Current.Privacy} to {value}?",
                    ["Cloud agents may then send your prompts and source code to their vendors.", "The change is recorded in the audit log."], "Switch"))
            { Raise(); return; }
            Message = _policy.Update(p => p with { Privacy = value }, "Privacy mode changed in Security").Message;
        }
    }

    public bool DisableThirdParty
    {
        get => _policy.Current.DisableThirdPartyExtensions;
        set
        {
            if (!value && !Dialogs.Confirm("Allow third-party extensions?", "Data-only extensions will load after review. Code extensions additionally need Development Mode.", null, "Allow"))
            { Raise(); return; }
            Message = _policy.Update(p => p with { DisableThirdPartyExtensions = value }, "Third-party extension switch changed").Message + " Restart to apply to loaded extensions.";
        }
    }

    public bool AllowRemoteRuntimes
    {
        get => _policy.Current.AllowRemoteRuntimes;
        set
        {
            if (value && !Dialogs.Confirm("Allow remote runtimes?", "Lets the app manage runtimes on other machines. Each endpoint must still be approved individually.", null, "Allow"))
            { Raise(); return; }
            Message = _policy.Update(p => p with { AllowRemoteRuntimes = value }, "Remote runtimes switch changed").Message;
        }
    }

    public bool SafeMode => _policy.Current.SafeMode;

    // ---- Network ----
    public ObservableCollection<string> Endpoints { get; } = [];
    public string? SelectedEndpoint { get => _selectedEndpoint; set => Set(ref _selectedEndpoint, value); }

    // ---- Protected paths ----
    public ObservableCollection<ProtectedPathEntry> Paths { get; } = [];
    public ProtectedPathEntry? SelectedPath { get => _selectedPath; set => Set(ref _selectedPath, value); }
    public bool RuleReadOnly { get => _ruleReadOnly; set => Set(ref _ruleReadOnly, value); }
    public bool RuleBlockAll { get => _ruleBlockAll; set => Set(ref _ruleBlockAll, value); }
    public bool RuleAskWrites { get => _ruleAskWrites; set => Set(ref _ruleAskWrites, value); }
    public bool RuleAskDelete { get => _ruleAskDelete; set => Set(ref _ruleAskDelete, value); }
    public bool RuleNoExec { get => _ruleNoExec; set => Set(ref _ruleNoExec, value); }
    public bool RuleNoAddOn { get => _ruleNoAddOn; set => Set(ref _ruleNoAddOn, value); }

    // ---- Credentials ----
    public ObservableCollection<CredentialReference> Credentials { get; } = [];
    public CredentialReference? SelectedCredential { get => _selectedCredential; set => Set(ref _selectedCredential, value); }

    // ---- Audit ----
    public ObservableCollection<AuditEvent> Audit { get; } = [];
    public string AuditStatus { get => _auditStatus; private set => Set(ref _auditStatus, value); }

    public ICommand AddPathCommand { get; }
    public ICommand RemovePathCommand { get; }
    public ICommand AddCredentialCommand { get; }
    public ICommand RemoveCredentialCommand { get; }
    public ICommand AddEndpointCommand { get; }
    public ICommand RevokeEndpointCommand { get; }
    public ICommand VerifyAuditCommand { get; }
    public ICommand RefreshAuditCommand { get; }

    protected override Task OnActivateAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private void RaisePolicy()
    {
        Raise(nameof(Privacy));
        Raise(nameof(DisableThirdParty));
        Raise(nameof(AllowRemoteRuntimes));
        Raise(nameof(SafeMode));
        Reload();
    }

    private void Reload()
    {
        Endpoints.Clear();
        foreach (var e in _policy.Current.ApprovedEndpoints) Endpoints.Add(e);
        Paths.Clear();
        foreach (var p in _paths.Entries) Paths.Add(p);
        Credentials.Clear();
        foreach (var c in _credentials.List()) Credentials.Add(c);
        Audit.Clear();
        foreach (var a in _audit.Recent(300)) Audit.Add(a);
    }

    private void AddPath()
    {
        if (Dialogs.Headless) return;
        var rules = ProtectedPathRule.None;
        if (RuleBlockAll) rules |= ProtectedPathRule.BlockAllAccess;
        if (RuleReadOnly) rules |= ProtectedPathRule.ReadOnly;
        if (RuleAskWrites) rules |= ProtectedPathRule.AskBeforeWrites;
        if (RuleAskDelete) rules |= ProtectedPathRule.AskBeforeDeletion;
        if (RuleNoExec) rules |= ProtectedPathRule.NoProcessExecution;
        if (RuleNoAddOn) rules |= ProtectedPathRule.NoAddOnAccess;
        if (rules == ProtectedPathRule.None) { Message = "Choose at least one rule first."; return; }
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to protect" };
        if (dlg.ShowDialog() != true) return;
        _paths.AddUserRule(dlg.FolderName, rules, Path.GetFileName(dlg.FolderName));
        Message = "Protected: " + dlg.FolderName;
        Reload();
    }

    private void RemovePath()
    {
        if (!Dialogs.Confirm("Remove protection", $"Stop protecting {SelectedPath!.Path}? This weakens security and is audited.", null, "Remove", danger: true)) return;
        _paths.RemoveUserRule(SelectedPath.Path);
        Reload();
    }

    private void AddCredential()
    {
        var name = Dialogs.Prompt("New credential", "NAME (shown in the app)", hint: "Stored in Windows Credential Manager. The value is never shown again, never logged and never exported.");
        if (string.IsNullOrWhiteSpace(name)) return;
        var kind = Dialogs.Prompt("New credential", "KIND (e.g. huggingface-token, or extension:<id> to share with one extension)", "huggingface-token") ?? "secret";
        var secret = Dialogs.Prompt("New credential", "SECRET VALUE", secret: true);
        if (string.IsNullOrEmpty(secret)) return;
        try
        {
            _credentials.Save(name, kind, secret);
            Message = $"Saved '{name}'.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Message = ex.Message; }
        Reload();
    }

    private void RemoveCredential()
    {
        if (!Dialogs.Confirm("Remove credential", $"Delete '{SelectedCredential!.DisplayName}' from Windows Credential Manager?", null, "Delete", danger: true)) return;
        _credentials.Remove(SelectedCredential.Id);
        Reload();
    }

    private void AddEndpoint()
    {
        var url = Dialogs.Prompt("Approve endpoint", "URL (scheme://host:port)", "https://",
            hint: "Non-loopback traffic is refused unless the endpoint is approved here. Plain HTTP is only accepted for local-network addresses.");
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) { Message = "Not a valid URL."; return; }
        Message = _policy.ApproveEndpoint(u, "Approved manually in Security → Network").Message;
        Reload();
    }

    private void VerifyAudit()
    {
        var v = _audit.Verify();
        AuditStatus = v.Message;
    }
}
