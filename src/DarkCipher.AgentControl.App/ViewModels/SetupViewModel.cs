using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using DarkCipher.AgentControl.App.Mvvm;
using DarkCipher.AgentControl.App.Shell;
using DarkCipher.AgentControl.Application.Compatibility;
using DarkCipher.AgentControl.Application.Downloads;
using DarkCipher.AgentControl.Application.Hardware;
using DarkCipher.AgentControl.Application.Recommendations;
using DarkCipher.AgentControl.Application.Sessions;
using DarkCipher.AgentControl.Application.Settings;
using DarkCipher.AgentControl.Application.Setup;
using DarkCipher.AgentControl.Application.Stacks;
using DarkCipher.AgentControl.Application.Tools;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Ollama;

namespace DarkCipher.AgentControl.App.ViewModels;

public enum SetupStep { Welcome, HardwareScan, SoftwareDetection, RecommendedSetup, ModelSelection, Plan, Installation, Verification, Ready }

/// <summary>"Get My Local Coding AI Ready": a guided, cancellable flow. Every change is previewed in the Plan step. Missing
/// Ollama and agents are installed automatically through the catalogue (winget, each with its own approval dialog), and a
/// stopped Ollama is started. On any failure the flow stops with Retry / Change model / View logs / Cancel; Retry
/// detects again and re-plans, so it never repeats a stale plan.</summary>
public sealed class SetupViewModel : PageViewModel
{
    private readonly HardwareService _hardware;
    private readonly OllamaRuntime _ollama;
    private readonly IEnumerable<IAgentProvider> _agents;
    private readonly RecommendationEngine _engine;
    private readonly DownloadManager _downloads;
    private readonly StackService _stacks;
    private readonly ProjectService _projects;
    private readonly SessionService _sessions;
    private readonly OllamaCompatibilitySuite _suite;
    private readonly GatewayProbe _gateway;
    private readonly CompatibilityStore _compat;
    private readonly AppSettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly IApprovalService _approval;
    private readonly Navigator _nav;
    private readonly AgentToolInstallationService _tools;
    private readonly OllamaLauncher _launcher;
    private SetupStep _step = SetupStep.Welcome;
    private CancellationTokenSource? _cts;
    private OneClickPlan _plan = OneClickPlan.Balanced;
    private string _agentId;
    private Recommendation? _selectedModel;
    private SetupPlan? _setupPlan;
    private RuntimeStatus? _runtime;
    private IReadOnlyList<AgentInstallation> _installs = [];
    private string? _createdStackId, _createdProjectId;
    private bool _failed;
    private readonly StringBuilder _details = new();

    public SetupViewModel(HardwareService hardware, OllamaRuntime ollama, IEnumerable<IAgentProvider> agents, RecommendationEngine engine, DownloadManager downloads,
        StackService stacks, ProjectService projects, SessionService sessions, OllamaCompatibilitySuite suite, GatewayProbe gateway, CompatibilityStore compat,
        AppSettingsService settings, IAppPaths paths, IApprovalService approval, Navigator nav, AgentToolInstallationService tools, OllamaLauncher launcher)
    {
        _tools = tools; _launcher = launcher;
        _hardware = hardware; _ollama = ollama; _agents = agents; _engine = engine; _downloads = downloads; _stacks = stacks; _projects = projects;
        _sessions = sessions; _suite = suite; _gateway = gateway; _compat = compat; _settings = settings; _paths = paths; _approval = approval; _nav = nav;
        _agentId = settings.Current.PreferredAgentId;
        NextCommand = new AsyncCommand(NextAsync, () => !Busy && CanNext);
        BackCommand = new RelayCommand(Back, () => !Busy && Step is > SetupStep.Welcome and not SetupStep.Installation and not SetupStep.Verification);
        CancelCommand = new RelayCommand(Cancel);
        RescanCommand = new AsyncCommand(() => Guard(() => EnterAsync(Step, rescan: true), "Rescan failed"), () => !Busy);
        DetailsCommand = new RelayCommand(() => Dialogs.ShowText("Technical details", _details.ToString()));
        StartSessionCommand = new AsyncCommand(StartSessionAsync, () => _createdStackId is not null && _createdProjectId is not null);
        ViewLogsCommand = new RelayCommand(() => nav.Go("logs"));
        ChangeModelCommand = new RelayCommand(() => { _failed = false; GoTo(SetupStep.ModelSelection); });
        RetryCommand = new AsyncCommand(RetryAsync, () => !Busy);
    }

    public override string Title => "Get My Local Coding AI Ready";
    public override string Subtitle => "Detect my hardware → recommend my stack → install → configure → test → ready to code.";

    public SetupStep Step { get => _step; private set { if (Set(ref _step, value)) { Raise(nameof(StepIndex)); Raise(nameof(CanNext)); Raise(nameof(NextLabel)); } } }
    public int StepIndex => (int)Step;
    public IReadOnlyList<string> StepNames { get; } = ["Welcome", "Hardware", "Software", "Recommended setup", "Model", "Plan", "Install", "Verify", "Ready"];
    public bool Failed { get => _failed; private set => Set(ref _failed, value); }

    public ObservableCollection<string> Lines { get; } = [];
    public ObservableCollection<StatusRow> Facts { get; } = [];
    public ObservableCollection<Recommendation> Recommendations { get; } = [];
    public ObservableCollection<PlannedOperation> Operations { get; } = [];
    public ObservableCollection<string> PlanNotes { get; } = [];

    public IReadOnlyList<OneClickPlan> Plans { get; } = Enum.GetValues<OneClickPlan>();
    public OneClickPlan Plan { get => _plan; set => Set(ref _plan, value); }
    public IReadOnlyList<KeyValuePair<string, string>> AgentChoices { get; } =
        [new("codex", "Codex CLI + Ollama (fully local, documented)"), new("claude-code", "Claude Code + local gateway (Experimental, unsupported by Anthropic)")];
    public string AgentId { get => _agentId; set => Set(ref _agentId, value); }
    public Recommendation? SelectedModel { get => _selectedModel; set { Set(ref _selectedModel, value); Raise(nameof(CanNext)); } }

    public bool CanNext => Step switch
    {
        SetupStep.ModelSelection => SelectedModel is not null || Plan == OneClickPlan.Hybrid,
        SetupStep.Ready => false,
        _ => !Failed,
    };

    public string NextLabel => Step switch
    {
        SetupStep.Welcome => "Start",
        SetupStep.Plan => "Approve and install",
        SetupStep.Verification => "Continue",
        _ => "Next",
    };

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RescanCommand { get; }
    public ICommand DetailsCommand { get; }
    public ICommand StartSessionCommand { get; }
    public ICommand ViewLogsCommand { get; }
    public ICommand ChangeModelCommand { get; }
    public ICommand RetryCommand { get; }

    private void Log(string line)
    {
        Ui.OnUi(() => Lines.Add(line));
        _details.AppendLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    }

    private void GoTo(SetupStep s)
    {
        Lines.Clear();
        Step = s;
    }

    private async Task NextAsync()
    {
        if (Step == SetupStep.Ready) return;
        var next = Step + 1;
        GoTo(next);
        await Guard(() => EnterAsync(next, rescan: false), $"{StepNames[(int)next]} failed");
        if (!string.IsNullOrEmpty(Message) && Step is SetupStep.Installation or SetupStep.Verification) Failed = true;
    }

    private void Back()
    {
        if (Step > SetupStep.Welcome) GoTo(Step - 1);
        Failed = false;
    }

    private void Cancel()
    {
        _cts?.Cancel();
        Failed = false;
        Message = "";
        GoTo(SetupStep.Welcome);
        Log("Setup cancelled. Anything already downloaded is kept; nothing else was changed.");
    }

    private async Task EnterAsync(SetupStep step, bool rescan)
    {
        Message = "";
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        switch (step)
        {
            case SetupStep.HardwareScan:
            {
                Facts.Clear();
                var hw = await _hardware.GetAsync(ct, rescan);
                var a = _hardware.Assessment!;
                Facts.Add(new StatusRow("Rating", a.Headline, "", StatusBrushes.Accent));
                foreach (var r in a.Reasons) Facts.Add(new StatusRow("Why", "", r, StatusBrushes.Muted));
                _details.AppendLine(HardwareAssessor.Summary(hw));
                break;
            }
            case SetupStep.SoftwareDetection:
            {
                Facts.Clear();
                if (rescan) foreach (var a in _agents) a.InvalidateDetection();
                _runtime = await _ollama.GetStatusAsync(ct);
                Facts.Add(new StatusRow("Ollama", _runtime.Health.ToString(), _runtime.Summary, StatusBrushes.For(_runtime.Health)));
                var list = new List<AgentInstallation>();
                foreach (var a in _agents)
                {
                    var d = await a.DetectAsync(ct);
                    list.Add(d);
                    Facts.Add(new StatusRow(a.DisplayName, d.Installed ? "Installed" : "Not installed", d.Summary + (d.Notes.Count > 0 ? " — " + string.Join(" ", d.Notes) : ""), StatusBrushes.For(d.Health)));
                }
                _installs = list;
                break;
            }
            case SetupStep.ModelSelection:
            {
                Recommendations.Clear();
                var hw = await _hardware.GetAsync(ct);
                IReadOnlyList<Domain.Models.ModelDescriptor> installed = [];
                IReadOnlyList<Domain.Models.RunningModel> running = [];
                if (_runtime?.Health is ComponentHealth.Healthy or ComponentHealth.Degraded)
                {
                    installed = await _ollama.ListModelsAsync(ct);
                    running = await _ollama.ListRunningAsync(ct);
                }
                var profile = SetupPlanner.ProfileFor(Plan);
                foreach (var r in _engine.Recommend(hw, profile, installed, 6, running)) Recommendations.Add(r);
                SelectedModel = Recommendations.FirstOrDefault();
                if (Recommendations.Count == 0) Log(Plan == OneClickPlan.Hybrid ? "Hybrid uses a cloud agent; no local model needed." : "No model fits this plan on this hardware. Try Lightweight or CPU-Only.");
                break;
            }
            case SetupStep.Plan:
                await BuildPlanAsync(redetect: rescan, ct);
                break;
            case SetupStep.Installation:
                await InstallAsync(ct);
                break;
            case SetupStep.Verification:
                await VerifyAsync(ct);
                break;
            case SetupStep.Ready:
                _settings.Update(s => s with { OnboardingCompleted = true, PreferredAgentId = AgentId, DefaultStackId = _createdStackId ?? s.DefaultStackId });
                Log("Your stack is ready. Start a session in the sample project, or add your own project in Projects.");
                break;
        }
    }

    private async Task RetryAsync()
    {
        Failed = false;
        GoTo(Step);
        await Guard(() => EnterAsync(Step, rescan: true), $"{StepNames[(int)Step]} failed");
        if (!string.IsNullOrEmpty(Message) && Step is SetupStep.Installation or SetupStep.Verification) Failed = true;
    }

    /// <summary>Detects Ollama and the agents again (optional) and rebuilds the plan, so installs always act on the
    /// current state of the PC rather than on what was true when the plan was first shown.</summary>
    private async Task BuildPlanAsync(bool redetect, CancellationToken ct)
    {
        if (redetect || _runtime is null)
        {
            foreach (var a in _agents) a.InvalidateDetection();
            _runtime = await _ollama.GetStatusAsync(ct);
            var list = new List<AgentInstallation>();
            foreach (var a in _agents) list.Add(await a.DetectAsync(ct));
            _installs = list;
        }
        var hw = await _hardware.GetAsync(ct);
        _setupPlan = SetupPlanner.Build(Plan, hw, _runtime, _installs, SelectedModel, AgentId,
            Composition.Bootstrapper.ModelDriveFreeBytes(), _paths.AgentProfilesDirectory, _paths.TestProjectsDirectory);
        Ui.OnUi(() =>
        {
            Operations.Clear();
            foreach (var o in _setupPlan.Operations) Operations.Add(o);
            PlanNotes.Clear();
            PlanNotes.Add($"Total download: {Bytes.Format(_setupPlan.TotalDownloadBytes)} · free on model drive: {Bytes.Format(_setupPlan.FreeDiskBytes)}");
            foreach (var c in _setupPlan.ConfigChanges) PlanNotes.Add(c);
            foreach (var w in _setupPlan.Warnings) PlanNotes.Add("⚠ " + w);
            if (_setupPlan.Operations.Any(o => o.ToolId is not null && o.Kind != PlannedOperationKind.StartRuntime && o.State == PlannedOperationState.Pending))
                PlanNotes.Add("Each install opens its own approval with the exact winget package, version, source and changes. Nothing is installed without it.");
            PlanNotes.Add("No administrator rights are needed. No system settings, firewall rules or Windows security settings are changed.");
        });
    }

    /// <summary>Installs a catalogue tool through the same path as the Catalogue page: plan → approval dialog → unified
    /// queue → winget → post-install test. Throws with the reason (and the official manual route) when it cannot.</summary>
    private async Task InstallToolAsync(PlannedOperation op, CancellationToken ct)
    {
        var id = op.ToolId!;
        Log($"Planning: {op.Title} (winget {SetupPlanner.WingetId(id)})…");
        var plan = await _tools.PlanAsync(id, InstallOperation.Install, ct);
        if (!plan.Success) throw new InvalidOperationException($"{op.Title} could not be planned: {plan.Message}");
        var job = await _tools.SubmitAsync(plan.Value!, _approval, ct);
        if (!job.Success) throw new InvalidOperationException($"{op.Title}: {job.Message}");
        var j = job.Value!;
        Log($"  {plan.Value!.DisplayName} {plan.Value.Version} from {plan.Value.Source} — queued (also visible in Downloads).");
        var last = "";
        while (!j.IsFinished)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);
            var line = $"  {j.StageLabel}: {j.Status}";
            if (line != last) { Log(line); last = line; }
        }
        if (j.Phase is not (DownloadPhase.Ready or DownloadPhase.ReadyWithLimitations))
            throw new InvalidOperationException($"{op.Title} did not finish: {j.Error ?? j.Status}. See Downloads for the full log.");
        Log($"  ✓ {op.Title}: {j.StageLabel}. {j.VerificationResult}");
        foreach (var a in _agents) a.InvalidateDetection();
    }

    private async Task StartRuntimeAsync(CancellationToken ct)
    {
        Log("Starting Ollama…");
        var r = await _launcher.EnsureRunningAsync(ct);
        if (!r.Success) throw new InvalidOperationException(r.Message);
        _runtime = r.Value;
        Log($"  ✓ {r.Message} {r.Value!.Summary}");
    }

    private async Task InstallAsync(CancellationToken ct)
    {
        await BuildPlanAsync(redetect: true, ct);
        var manual = _setupPlan!.Operations.Where(o => o.State == PlannedOperationState.ManualActionRequired).ToList();
        if (manual.Count > 0)
        {
            foreach (var m in manual) Log($"Action needed: {m.Title} — {m.Detail} {m.Source}");
            throw new InvalidOperationException("Some components must be installed by you first (see above). Then press Retry.");
        }

        foreach (var op in _setupPlan.Operations.Where(o => o.State == PlannedOperationState.Pending))
        {
            switch (op.Kind)
            {
                case PlannedOperationKind.InstallRuntime:
                    await InstallToolAsync(op, ct);
                    // Ollama's installer starts the app; wait for the server, and start it ourselves if it did not.
                    _runtime = await _launcher.WaitForServerAsync(TimeSpan.FromSeconds(30), ct);
                    if (_runtime.Health is not (ComponentHealth.Healthy or ComponentHealth.Degraded)) await StartRuntimeAsync(ct);
                    break;
                case PlannedOperationKind.StartRuntime:
                    await StartRuntimeAsync(ct);
                    break;
                case PlannedOperationKind.InstallAgent:
                    await InstallToolAsync(op, ct);
                    var installed = await _agents.First(a => a.Id == AgentId).DetectAsync(ct);
                    if (!installed.Installed) throw new InvalidOperationException($"{op.Title} finished, but {SetupPlanner.AgentDisplayName(AgentId)} is still not detected. Press Retry, or see Downloads.");
                    Log($"  {installed.Summary}");
                    break;
            }
        }

        if (SelectedModel is { Installed: false } rec)
        {
            Log($"Downloading {rec.Model.OllamaName} through Ollama…");
            var q = _downloads.Enqueue(new DownloadRequest("ollama", rec.Model.OllamaName, rec.Model.DisplayName, rec.Model.ApproxSizeBytes));
            if (!q.Success) throw new InvalidOperationException(q.Message);
            var job = q.Value!;
            while (!job.IsFinished)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct);
                if (job.Percent is { } p) Ui.OnUi(() => { if (Lines.Count > 0 && Lines[^1].StartsWith("  ", StringComparison.Ordinal)) Lines[^1] = $"  {job.Status} {p:0}%"; else Lines.Add($"  {job.Status} {p:0}%"); });
            }
            if (job.Phase != DownloadPhase.Completed) throw new InvalidOperationException("Download did not complete: " + (job.Error ?? job.Status));
            Log("Download verified by Ollama and listed as installed.");
        }

        var agentId = AgentId;
        var model = SelectedModel?.Model.OllamaName;
        var mode = Plan == OneClickPlan.Hybrid ? AgentMode.Native : agentId == "codex" ? AgentMode.LocalProvider : AgentMode.ExperimentalLocalGateway;
        var id = "my-" + agentId + "-" + DateTime.Now.ToString("yyyyMMddHHmm");
        var stack = new StackProfile
        {
            Id = id, Name = $"My {(agentId == "codex" ? "Codex" : "Claude Code")} stack ({Plan})", AgentProviderId = agentId, AgentMode = mode,
            RuntimeId = mode == AgentMode.Native ? null : "ollama", Endpoint = mode == AgentMode.Native ? null : _settings.Current.OllamaEndpoint,
            PrimaryModel = mode == AgentMode.Native ? null : model,
            ContextLength = mode == AgentMode.Native ? null : agentId == "codex" ? 32768 : 65536,
            PermissionProfileId = BuiltInPermissionProfiles.AskBeforeChangesId,
            Privacy = mode == AgentMode.Native ? PrivacyMode.Hybrid : PrivacyMode.FullyLocal,
            Description = "Created by Quick Setup.",
        };
        var saved = _stacks.Save(stack);
        if (!saved.Success) throw new InvalidOperationException("Could not create the stack: " + saved.Message);
        _createdStackId = id;
        Log($"Created stack '{stack.Name}' (permissions: Ask Before Changes).");

        var dir = TestProjectFactory.Create(_paths.TestProjectsDirectory);
        var added = _projects.Add(dir, "Sample project");
        if (!added.Success) throw new InvalidOperationException(added.Message);
        _createdProjectId = added.Value!.Id;
        Log($"Created a disposable sample project at {dir}.");
    }

    private async Task VerifyAsync(CancellationToken ct)
    {
        var model = SelectedModel?.Model.OllamaName;
        if (model is null) { Log("No local model to test (cloud stack)."); return; }
        var hw = await _hardware.GetAsync(ct);
        Log($"Running compatibility suite {OllamaCompatibilitySuite.SuiteVersion} against {model} (fixed test prompts, on this PC only)…");
        var report = await _suite.RunAsync(model, HardwareAssessor.Summary(hw), AgentId == "codex" ? 32768 : 65536, new Progress<string>(t => Log("  • " + t)), ct);
        _compat.Add(report);
        foreach (var r in report.Results) Log($"  {(r.Passed ? "PASS" : r.Required ? "FAIL" : "warn")}  {r.Test}: {r.Detail}");
        if (report.MeasuredTokensPerSecond is { } tps) Log($"Measured generation speed in this run: {tps:0.0} tokens/s (from Ollama's counters).");

        if (AgentId == "claude-code")
        {
            Log("Testing the Anthropic-compatible gateway (text + tool use)…");
            var gw = await _gateway.ProbeAnthropicAsync(model, ct);
            _compat.AddGateway(new CompatibilityStore.GatewayResult("claude-code", AgentProtocol.AnthropicMessages, model, report.ModelDigest, gw.Compatibility, DateTimeOffset.UtcNow,
                $"Ollama {gw.Version}: {gw.Compatibility}"));
            Log($"Gateway: {gw.Compatibility}. " + string.Join(" ", gw.Limitations.Take(2)));
            if (gw.Compatibility == CompatibilityVerdict.AgenticCompatible && _createdStackId is not null && _stacks.Find(_createdStackId) is { } s)
                _stacks.Save(s with { AgentMode = AgentMode.VerifiedLocalGateway });
        }

        if (report.Verdict != CompatibilityVerdict.AgenticCompatible)
            throw new InvalidOperationException(report.Verdict == CompatibilityVerdict.TextGenerationOnly
                ? "Text Generation Available / Agentic Compatibility Failed. Choose another model (Change model) or continue at your own risk from Stacks."
                : "The model failed the compatibility test.");
        Log("All required agentic tests passed.");
    }

    private async Task StartSessionAsync()
    {
        if (_createdStackId is null || _createdProjectId is null) return;
        var r = await _sessions.StartAsync(_createdStackId, _createdProjectId, _approval, CancellationToken.None);
        Message = r.Message;
        if (r.Success) _nav.Go("sessions");
    }
}
