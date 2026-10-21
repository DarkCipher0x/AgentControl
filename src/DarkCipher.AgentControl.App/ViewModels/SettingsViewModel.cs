using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Settings;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Updates;

namespace DarkCipher.AgentControl.App.ViewModels;

public sealed class SettingsViewModel : PageViewModel
{
    private readonly AppSettingsService _settings;
    private readonly ICredentialStore _credentials;
    private readonly IProcessExecutor _processes;
    private readonly IAppPaths _paths;
    private readonly UpdateSecurityService _updates;
    private string _endpoint;
    private string _preferredAgent;

    public SettingsViewModel(AppSettingsService settings, ICredentialStore credentials, IProcessExecutor processes, IAppPaths paths, UpdateSecurityService updates,
        AppearanceSettingsViewModel appearance)
    {
        Appearance = appearance;
        _settings = settings;
        _credentials = credentials;
        _processes = processes;
        _paths = paths;
        _updates = updates;
        _endpoint = settings.Current.OllamaEndpoint;
        _preferredAgent = settings.Current.PreferredAgentId;
        SaveCommand = new RelayCommand(() => Message = _settings.Update(s => s with { OllamaEndpoint = Endpoint.Trim(), PreferredAgentId = PreferredAgent }).Message);
        SetHfTokenCommand = new RelayCommand(SetHfToken);
        ClearHfTokenCommand = new RelayCommand(ClearHfToken, () => _settings.Current.HuggingFaceCredentialId is not null);
        OpenDataCommand = new AsyncCommand(OpenDataAsync);
        ResetOnboardingCommand = new RelayCommand(() => Message = _settings.Update(s => s with { OnboardingCompleted = false }).Message);
    }

    public override string Title => "Settings";
    public override string Subtitle => "Behaviour, appearance, navigation, dashboard and console. Security options live in Security.";

    /// <summary>Appearance, layout, navigation, dashboard, console, import/export and About.</summary>
    public AppearanceSettingsViewModel Appearance { get; }

    public string Endpoint { get => _endpoint; set => Set(ref _endpoint, value); }
    public string PreferredAgent { get => _preferredAgent; set => Set(ref _preferredAgent, value); }
    public IReadOnlyList<string> Agents { get; } = ["codex", "claude-code"];
    public string HfTokenStatus => _settings.Current.HuggingFaceCredentialId is { } id && _credentials.Exists(id) ? "Stored in Windows Credential Manager" : "Not set (public repositories only)";
    public string DataRoot => _paths.DataRoot;
    public string UpdateStatus => _updates.UpdatesEnabled ? "Signed updates enabled." : "Updates are switched off: no release-signing key exists yet, and unsigned updates are never installed.";

    public ICommand SaveCommand { get; }
    public ICommand SetHfTokenCommand { get; }
    public ICommand ClearHfTokenCommand { get; }
    public ICommand OpenDataCommand { get; }
    public ICommand ResetOnboardingCommand { get; }

    private void SetHfToken()
    {
        var token = Dialogs.Prompt("Hugging Face token", "TOKEN (read-only scope recommended)", secret: true,
            hint: "Only needed for rate limits on search. Stored in Windows Credential Manager; sent only to huggingface.co over HTTPS.");
        if (string.IsNullOrWhiteSpace(token)) return;
        var r = _credentials.Save("Hugging Face token", "huggingface-token", token.Trim());
        _settings.Update(s => s with { HuggingFaceCredentialId = r.Id });
        Raise(nameof(HfTokenStatus));
        Message = "Token stored.";
    }

    private void ClearHfToken()
    {
        if (_settings.Current.HuggingFaceCredentialId is { } id) _credentials.Remove(id);
        _settings.Update(s => s with { HuggingFaceCredentialId = null });
        Raise(nameof(HfTokenStatus));
    }

    private async Task OpenDataAsync()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var r = await _processes.RunAsync(new ProcessSpec { Executable = explorer, Arguments = [DataRoot], Timeout = TimeSpan.FromSeconds(10), Purpose = "Open data folder" }, CancellationToken.None);
        if (r.Outcome == ProcessOutcome.Blocked) Message = r.Message;
    }
}
