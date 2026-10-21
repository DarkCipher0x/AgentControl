using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Sdk;

namespace DarkCipher.AgentControl.Extensions;

/// <summary>
/// The only gateway from an extension into the application. Every call: permission check against the user-granted set,
/// security-policy check (Safe Mode, third-party switch), audit on denial and on privileged use, exceptions contained.
/// Processes started for an extension are evaluated with the Extension actor (stricter protected-path rules), are
/// always captured (no windows), time-limited to 60 s and never receive approvals.
/// </summary>
public sealed class SdkCapabilityBroker : ISdkCapabilityBroker
{
    private readonly string _extensionId;
    private readonly IReadOnlySet<ExtensionPermission> _granted;
    private readonly IExtensionHostServices _services;
    private readonly ISecurityPolicyService _policy;
    private readonly IAuditLogger _audit;
    private readonly IAppLogger _log;
    private readonly IProcessExecutor _processes;
    private readonly IReadOnlyList<IModelRuntime> _runtimes;
    private readonly IHardwareDiscoveryProvider _hardware;
    private readonly ICredentialStore _credentials;
    private readonly IDocumentStore _store;
    private readonly ExtensionContributionRegistry _registry;
    private readonly Action<SdkHealth, string> _healthSink;

    public SdkCapabilityBroker(string extensionId, IReadOnlySet<ExtensionPermission> granted, IExtensionHostServices services, ISecurityPolicyService policy,
        IAuditLogger audit, IAppLogger log, IProcessExecutor processes, IEnumerable<IModelRuntime> runtimes, IHardwareDiscoveryProvider hardware,
        ICredentialStore credentials, IDocumentStore store, ExtensionContributionRegistry registry, Action<SdkHealth, string> healthSink)
    {
        _extensionId = extensionId;
        _granted = granted;
        _services = services;
        _policy = policy;
        _audit = audit;
        _log = log;
        _processes = processes;
        _runtimes = runtimes.ToList();
        _hardware = hardware;
        _credentials = credentials;
        _store = store;
        _registry = registry;
        _healthSink = healthSink;
    }

    private void Require(ExtensionPermission p, string capability)
    {
        if (_policy.Current.SafeMode) Deny(capability, "Safe Mode is on.");
        if (!_granted.Contains(p)) Deny(capability, $"permission '{p}' was not granted.");
    }

    private void Deny(string capability, string why)
    {
        _audit.Record(new AuditEvent("ExtensionCapabilityDenied", "SdkCapabilityBroker", AuditResult.Denied, _extensionId, SecurityClassification.Medium, Detail: $"{capability}: {why}"));
        throw new CapabilityDeniedException($"{capability} denied: {why}");
    }

    public void RegisterStackTemplate(SdkStackTemplate template)
    {
        if (_policy.Current.SafeMode) Deny(nameof(RegisterStackTemplate), "Safe Mode is on.");
        _services.RegisterStackTemplate(_extensionId, template);
    }

    public void RegisterCommand(SdkCommand command)
    {
        Require(ExtensionPermission.UI, nameof(RegisterCommand));
        _registry.AddCommand(_extensionId, command);
    }

    public void RegisterDashboardPanel(SdkDashboardPanel panel)
    {
        Require(ExtensionPermission.UI, nameof(RegisterDashboardPanel));
        _registry.AddPanel(_extensionId, panel);
    }

    public async Task<IReadOnlyList<SdkModelInfo>> GetInstalledModelsAsync(CancellationToken ct)
    {
        Require(ExtensionPermission.ModelManagement, nameof(GetInstalledModelsAsync));
        var list = new List<SdkModelInfo>();
        foreach (var r in _runtimes)
        {
            try
            {
                foreach (var m in await r.ListModelsAsync(ct).ConfigureAwait(false))
                    list.Add(new SdkModelInfo(m.Name, m.Family, m.ParameterSize, m.Quantization, m.SizeBytes, m.ContextLength,
                        Enum.GetValues<ModelCapabilities>().Where(c => c != ModelCapabilities.None && m.Supports(c)).Select(c => c.ToString()).ToList()));
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException) { }
        }
        return list;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetHardwareSummaryAsync(CancellationToken ct)
    {
        Require(ExtensionPermission.Hardware, nameof(GetHardwareSummaryAsync));
        var hw = await _hardware.ScanAsync(ct).ConfigureAwait(false);
        var gpu = hw.PrimaryGpu;
        return new Dictionary<string, string>
        {
            ["cpu"] = hw.Cpu.Name,
            ["logicalProcessors"] = hw.Cpu.LogicalProcessors.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ramBytes"] = hw.Memory.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["gpu"] = gpu?.Name ?? "none",
            ["gpuMemoryBytes"] = (gpu?.DedicatedMemoryBytes ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    public async Task<SdkProcessResult> RunApprovedProcessAsync(SdkProcessRequest request, CancellationToken ct)
    {
        Require(ExtensionPermission.ProcessExecution, nameof(RunApprovedProcessAsync));
        var spec = new ProcessSpec
        {
            Executable = request.Executable,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            Timeout = request.Timeout > TimeSpan.FromSeconds(60) || request.Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(60) : request.Timeout,
            Purpose = $"[extension {_extensionId}] {request.Purpose}",
            Mode = ProcessMode.Captured,
            Actor = PathActor.Extension,
            PathApprovalGranted = false,
            MaxOutputChars = 64 * 1024,
        };
        _audit.Record(new AuditEvent("ExtensionProcessRequested", "SdkCapabilityBroker", AuditResult.Success, request.Executable, SecurityClassification.High, Detail: $"{_extensionId}: {request.Purpose}"));
        var r = await _processes.RunAsync(spec, ct).ConfigureAwait(false);
        return new SdkProcessResult(r.Succeeded, r.ExitCode, r.StdOut, r.StdErr, r.Message);
    }

    public Task<string> SubmitModelDownloadAsync(string modelName, CancellationToken ct)
    {
        Require(ExtensionPermission.ModelManagement, nameof(SubmitModelDownloadAsync));
        if (!ModelNameRules.IsValid(modelName)) Deny(nameof(SubmitModelDownloadAsync), "invalid model name.");
        return _services.SubmitModelDownloadAsync(_extensionId, modelName, ct);
    }

    public SdkCredentialHandle? GetCredentialReference(string name)
    {
        Require(ExtensionPermission.CredentialReferences, nameof(GetCredentialReference));
        // Only credentials the user explicitly created FOR this extension (kind "extension:<id>") are visible.
        var c = _credentials.List().FirstOrDefault(r => r.Kind == "extension:" + _extensionId && string.Equals(r.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        return c is null ? null : new SdkCredentialHandle(c.Id, c.DisplayName);
    }

    private string SettingsName => "ext-settings-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(_extensionId)))[..16].ToLowerInvariant();

    public string? GetSetting(string key)
    {
        Require(ExtensionPermission.Settings, nameof(GetSetting));
        return _store.Load(SettingsName, () => new ExtensionSettings()).Values.GetValueOrDefault(key);
    }

    public void SetSetting(string key, string value)
    {
        Require(ExtensionPermission.Settings, nameof(SetSetting));
        if (string.IsNullOrWhiteSpace(key) || key.Length > 64 || value.Length > 4096) Deny(nameof(SetSetting), "key or value too long.");
        var s = _store.Load(SettingsName, () => new ExtensionSettings());
        if (!s.Values.ContainsKey(key) && s.Values.Count >= 100) Deny(nameof(SetSetting), "too many settings.");
        s.Values[key] = value;
        _store.Save(SettingsName, s);
    }

    public void Log(SdkLogLevel level, string message)
    {
        Require(ExtensionPermission.Logging, nameof(Log));
        var lvl = level switch { SdkLogLevel.Error => LogLevel.Error, SdkLogLevel.Warning => LogLevel.Warning, SdkLogLevel.Debug => LogLevel.Debug, _ => LogLevel.Information };
        _log.Log(lvl, "ext:" + _extensionId, message.Length > 2000 ? message[..2000] : message);
    }

    public void ReportHealth(SdkHealth health, string summary) => _healthSink(health, summary.Length > 300 ? summary[..300] : summary);

    // ---- SDK 1.1 ----

    public void RegisterTemplate(string templateJson)
    {
        if (_policy.Current.SafeMode) Deny(nameof(RegisterTemplate), "Safe Mode is on.");
        if (templateJson is null || templateJson.Length > 64 * 1024) Deny(nameof(RegisterTemplate), "template too large.");
        _services.RegisterTemplate(_extensionId, templateJson!);
    }

    public void RegisterAgentTool(SdkAgentToolEntry tool)
    {
        Require(ExtensionPermission.CatalogueContributions, nameof(RegisterAgentTool));
        _services.RegisterAgentTool(_extensionId, tool);
        _audit.Record(new AuditEvent("ExtensionCatalogueEntry", "SdkCapabilityBroker", AuditResult.Success, tool.WingetPackageId, SecurityClassification.Medium, Detail: _extensionId));
    }

    public void RegisterModelSource(ISdkModelSource source)
    {
        Require(ExtensionPermission.CatalogueContributions, nameof(RegisterModelSource));
        Require(ExtensionPermission.Network, nameof(RegisterModelSource));
        if (source is null || string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 64) Deny(nameof(RegisterModelSource), "invalid source id.");
        _registry.AddModelSource(_extensionId, source!);
    }

    public void RegisterCompatibilityRule(SdkCompatibilityRule rule)
    {
        Require(ExtensionPermission.CatalogueContributions, nameof(RegisterCompatibilityRule));
        if (rule is null || rule.Effect is not ("warn" or "block") || rule.Message.Length > 300 || (rule.ModelPattern?.Length ?? 0) > 120)
            Deny(nameof(RegisterCompatibilityRule), "invalid rule (effect must be 'warn' or 'block'; message up to 300 characters).");
        _registry.AddRule(_extensionId, rule!);
    }

    public void RegisterHealthCheck(SdkHealthCheck check)
    {
        Require(ExtensionPermission.UI, nameof(RegisterHealthCheck));
        _registry.AddHealthCheck(_extensionId, check);
    }

    public void RegisterSettingsPanel(SdkSettingsPanel panel)
    {
        Require(ExtensionPermission.Settings, nameof(RegisterSettingsPanel));
        if (panel.Fields.Count > 40 || panel.Fields.Any(f => f.Key.Length > 64)) Deny(nameof(RegisterSettingsPanel), "too many or invalid fields.");
        _registry.AddSettingsPanel(_extensionId, panel);
    }

    public sealed class ExtensionSettings
    {
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
    }
}

/// <summary>Commands and dashboard panels contributed by extensions; invoked with a timeout and isolated exceptions.</summary>
public sealed class ExtensionContributionRegistry
{
    private readonly object _gate = new();
    private readonly List<(string ExtensionId, SdkCommand Command)> _commands = [];
    private readonly List<(string ExtensionId, SdkDashboardPanel Panel)> _panels = [];

    public event EventHandler? Changed;

    public IReadOnlyList<(string ExtensionId, SdkCommand Command)> Commands { get { lock (_gate) return [.. _commands]; } }
    public IReadOnlyList<(string ExtensionId, SdkDashboardPanel Panel)> Panels { get { lock (_gate) return [.. _panels]; } }

    private readonly List<(string ExtensionId, ISdkModelSource Source)> _sources = [];
    private readonly List<(string ExtensionId, SdkCompatibilityRule Rule)> _rules = [];
    private readonly List<(string ExtensionId, SdkHealthCheck Check)> _checks = [];
    private readonly List<(string ExtensionId, SdkSettingsPanel Panel)> _settings = [];

    public IReadOnlyList<(string ExtensionId, ISdkModelSource Source)> ModelSources { get { lock (_gate) return [.. _sources]; } }
    public IReadOnlyList<(string ExtensionId, SdkCompatibilityRule Rule)> Rules { get { lock (_gate) return [.. _rules]; } }
    public IReadOnlyList<(string ExtensionId, SdkHealthCheck Check)> HealthChecks { get { lock (_gate) return [.. _checks]; } }
    public IReadOnlyList<(string ExtensionId, SdkSettingsPanel Panel)> SettingsPanels { get { lock (_gate) return [.. _settings]; } }

    internal void AddCommand(string ext, SdkCommand c) { lock (_gate) { _commands.RemoveAll(x => x.ExtensionId == ext && x.Command.Id == c.Id); _commands.Add((ext, c)); } Changed?.Invoke(this, EventArgs.Empty); }
    internal void AddPanel(string ext, SdkDashboardPanel p) { lock (_gate) { _panels.RemoveAll(x => x.ExtensionId == ext && x.Panel.Id == p.Id); _panels.Add((ext, p)); } Changed?.Invoke(this, EventArgs.Empty); }
    internal void AddModelSource(string ext, ISdkModelSource s) { lock (_gate) { _sources.RemoveAll(x => x.ExtensionId == ext && x.Source.Id == s.Id); _sources.Add((ext, s)); } Changed?.Invoke(this, EventArgs.Empty); }
    internal void AddRule(string ext, SdkCompatibilityRule r) { lock (_gate) { _rules.RemoveAll(x => x.ExtensionId == ext && x.Rule.Id == r.Id); _rules.Add((ext, r)); } Changed?.Invoke(this, EventArgs.Empty); }
    internal void AddHealthCheck(string ext, SdkHealthCheck c) { lock (_gate) { _checks.RemoveAll(x => x.ExtensionId == ext && x.Check.Id == c.Id); _checks.Add((ext, c)); } Changed?.Invoke(this, EventArgs.Empty); }
    internal void AddSettingsPanel(string ext, SdkSettingsPanel p) { lock (_gate) { _settings.RemoveAll(x => x.ExtensionId == ext && x.Panel.Id == p.Id); _settings.Add((ext, p)); } Changed?.Invoke(this, EventArgs.Empty); }

    public void RemoveExtension(string ext)
    {
        lock (_gate)
        {
            _commands.RemoveAll(x => x.ExtensionId == ext); _panels.RemoveAll(x => x.ExtensionId == ext); _sources.RemoveAll(x => x.ExtensionId == ext);
            _rules.RemoveAll(x => x.ExtensionId == ext); _checks.RemoveAll(x => x.ExtensionId == ext); _settings.RemoveAll(x => x.ExtensionId == ext);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Runs every extension health check (5 s each, isolated).</summary>
    public async Task<IReadOnlyList<(string ExtensionId, string Title, SdkHealth Health, string Detail)>> RunHealthChecksAsync()
    {
        var results = new List<(string, string, SdkHealth, string)>();
        foreach (var (ext, check) in HealthChecks)
        {
            SdkHealthResult? r = null;
            var (ok, text) = await InvokeIsolatedAsync(async ct => { r = await check.Run(ct).ConfigureAwait(false); return r.Detail; }, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            results.Add((ext, check.Title, ok && r is not null ? r.Health : SdkHealth.Failed, text.Length > 300 ? text[..300] : text));
        }
        return results;
    }

    /// <summary>Wildcard match for compatibility rules ('*' only).</summary>
    public static bool Matches(string? pattern, string? value)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        if (value is null) return false;
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, rx, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    public static async Task<(bool Ok, string Result)> InvokeIsolatedAsync(Func<CancellationToken, Task<string>> f, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var task = f(cts.Token);
            var done = await Task.WhenAny(task, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false);
            if (done != task) return (false, $"Timed out after {timeout.TotalSeconds:0} s.");
            return (true, await task.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (false, $"{ex.GetType().Name}: {ex.Message}"); }
    }
}
