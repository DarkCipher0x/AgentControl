using System.Text;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Integrations.Common;

namespace DarkCipher.AgentControl.Integrations.Agents;

/// <summary>
/// OpenAI Codex CLI. Local mode uses Codex's documented OSS provider ("--oss" with oss_provider = "ollama") inside an
/// ISOLATED profile: CODEX_HOME points at an app-managed directory with its own config.toml, so the user's ~/.codex is
/// never read or modified. Keys used are documented (model, oss_provider, model_context_window, sandbox_mode,
/// approval_policy). "danger-full-access" and approval "never" are never generated.
/// </summary>
public sealed class CodexProvider : IAgentProvider, IAgentEndToEndProbe
{
    public const string ProviderId = "codex";
    private readonly IProcessExecutor _processes;

    public CodexProvider(IProcessExecutor processes) => _processes = processes;

    public string Id => ProviderId;
    public string DisplayName => "Codex CLI";
    public string Vendor => "OpenAI";
    public FeatureMaturity Maturity => FeatureMaturity.Supported;

    public IReadOnlyList<AgentModeInfo> Modes { get; } =
    [
        new(AgentMode.LocalProvider, "Local model via Ollama (--oss)", FeatureMaturity.Supported, AgentProtocol.OpenAIResponses,
            "Codex's documented local-provider mode with Ollama, in an isolated profile managed by this app."),
        new(AgentMode.Native, "OpenAI (native)", FeatureMaturity.Supported, AgentProtocol.ProviderNative,
            "Codex with your own OpenAI/ChatGPT login and your normal ~/.codex configuration. Prompts and code are sent to OpenAI."),
    ];

    public static IEnumerable<string> CandidatePaths()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(profile, ".local", "bin", "codex.exe");
        yield return Path.Combine(local, "Programs", "codex", "codex.exe");
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "codex.exe");
        // winget package folder (no Links alias on some PCs); the entry point is named in codex-package.json.
        foreach (var p in WingetPackages.Candidates("OpenAI.Codex", "codex.exe")) yield return p;
    }

    private readonly DetectionCache _cache = new();

    public void InvalidateDetection() => _cache.Invalidate();

    public Task<AgentInstallation> DetectAsync(CancellationToken ct) => _cache.GetAsync(DetectCoreAsync, ct);

    private async Task<AgentInstallation> DetectCoreAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        var exe = ExecutableLocator.Find("codex.exe", CandidatePaths());
        if (exe is null)
        {
            var shim = ExecutableLocator.FindShimOnPath("codex");
            if (shim is not null) notes.Add($"Found a script shim at {shim}. It is not launched (batch files cannot be quoted safely); install a native codex.exe.");
            notes.Add("Install Codex from the Catalogue or Quick Setup (winget OpenAI.Codex, per-user), then rescan.");
            return new AgentInstallation(Id, false, null, null, ComponentHealth.Unavailable, "Codex CLI is not installed.", notes);
        }
        var r = await _processes.RunAsync(new ProcessSpec { Executable = exe, Arguments = ["--version"], Timeout = TimeSpan.FromSeconds(20), Purpose = "Detect Codex version" }, ct).ConfigureAwait(false);
        var version = r.Succeeded ? r.StdOut.Split('\n').FirstOrDefault()?.Trim() : null;
        return new AgentInstallation(Id, true, exe, version, r.Succeeded ? ComponentHealth.Healthy : r.Outcome == ProcessOutcome.Blocked ? ComponentHealth.SecurityBlock : ComponentHealth.Degraded,
            r.Succeeded ? $"Codex {version}" : "Codex is installed but did not report a version: " + r.Message, notes);
    }

    public AgentLaunchPlan BuildLaunchPlan(AgentLaunchRequest request)
    {
        if (!request.Installation.Installed || request.Installation.ExecutablePath is null)
            throw new InvalidOperationException("Codex is not installed.");

        var warnings = new List<string>();
        var files = new List<PlannedFileChange>();
        var env = new Dictionary<string, string>();
        var args = new List<string>();
        string backend;

        var (sandbox, approval) = SandboxFor(request.Permissions);

        if (request.Mode == AgentMode.LocalProvider)
        {
            var model = ModelNameRules.Require(request.Model);
            if (request.ProfileDirectory is null) throw new InvalidOperationException("An isolated profile directory is required for local mode.");
            if (request.Endpoint is { } ep && !(ep.IsLoopback && ep.Port == 11434))
                warnings.Add("Codex's built-in Ollama provider uses http://localhost:11434. Custom endpoints are not configured by this app yet (Planned).");
            var ctx = request.ContextLength ?? 32768;
            if (ctx < 32768) warnings.Add($"Context {ctx:N0} is small for agentic coding; Codex works best with 32K or more.");

            env["CODEX_HOME"] = request.ProfileDirectory;
            files.Add(new PlannedFileChange(Path.Combine(request.ProfileDirectory, "config.toml"), ConfigToml(model, ctx, sandbox, approval, Reasoning(request)),
                "Isolated Codex profile (your ~/.codex is not used or changed)."));
            args.Add("--oss");
            args.Add("-m");
            args.Add(model);
            backend = $"Local model {model} via Ollama (Codex OSS provider, http://localhost:11434/v1) — prompts stay on this PC";
        }
        else if (request.Mode == AgentMode.Native)
        {
            args.Add("--sandbox");
            args.Add(sandbox);
            args.Add("--ask-for-approval");
            args.Add(approval);
            if (Reasoning(request) is { } effort) { args.Add("-c"); args.Add($"model_reasoning_effort=\"{effort}\""); }
            backend = "OpenAI (native) — prompts and code leave this PC; uses your own ~/.codex configuration";
        }
        else throw new InvalidOperationException($"Codex does not support the {request.Mode} mode.");
        if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
            warnings.Add("The template's extra instructions are not passed to Codex (no supported launch option); add them to AGENTS.md in the project if you need them.");

        warnings.Add("Codex's sandbox is enforced by Codex itself; on Windows its sandbox is newer than on macOS/Linux. This app only enforces the launch folder.");

        var spec = new ProcessSpec
        {
            Executable = request.Installation.ExecutablePath,
            Arguments = args,
            WorkingDirectory = request.ProjectDirectory,
            Environment = env,
            Mode = ProcessMode.InteractiveConsole,
            Purpose = $"Codex session ({request.Mode})",
            Actor = PathActor.Agent,
        };
        return new AgentLaunchPlan(spec, request.Mode, backend, FeatureMaturity.Supported, warnings, files, DescribeEnforcement(request.Permissions, request.Mode));
    }

    /// <summary>Headless end-to-end probe using Codex's non-interactive "exec" subcommand with the same options as a
    /// session. NOT yet verified against a real Codex install by this app (Codex is not installed on the build PC).</summary>
    public EndToEndProbe BuildProbe(AgentLaunchRequest request, string expectedToken)
    {
        var plan = BuildLaunchPlan(request);
        var spec = plan.Process with
        {
            Mode = ProcessMode.Captured,
            Arguments = ["exec", .. plan.Process.Arguments, "--skip-git-repo-check", $"Reply with exactly the word {expectedToken} and nothing else."],
            Timeout = TimeSpan.FromMinutes(5),
            Purpose = "End-to-end verification (Codex exec, headless)",
        };
        return new EndToEndProbe(spec, expectedToken, "Codex answers a fixed prompt with 'codex exec' in a disposable project.");
    }

    internal static (string Sandbox, string Approval) SandboxFor(PermissionProfile p) => p.Id switch
    {
        BuiltInPermissionProfiles.ReadOnlyId => ("read-only", "on-request"),
        BuiltInPermissionProfiles.AskBeforeChangesId => ("read-only", "on-request"),
        _ when p.Get(AgentPermission.FileWrite) == PermissionGrant.Allow => ("workspace-write", "on-request"),
        _ => ("read-only", "on-request"),
    };

    internal static string ConfigToml(string model, int context, string sandbox, string approval, string? reasoning = null)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("# Managed by DarkCipher AgentControl. Isolated Codex profile: the keys below are kept in step with the stack;");
        sb.AppendLine("# other settings you add are kept. Changing one of these keys yourself is reported as a conflict before launch.");
        sb.AppendLine($"model = {Q(model)}");
        sb.AppendLine("oss_provider = \"ollama\"");
        sb.AppendLine($"model_context_window = {context}");
        sb.AppendLine($"sandbox_mode = {Q(sandbox)}");
        sb.AppendLine($"approval_policy = {Q(approval)}");
        if (reasoning is not null) sb.AppendLine($"model_reasoning_effort = {Q(reasoning)}");
        return sb.ToString();
    }

    /// <summary>The template's reasoning option, if valid (low/medium/high → Codex model_reasoning_effort).</summary>
    internal static string? Reasoning(AgentLaunchRequest r) =>
        r.Options is { } o && o.TryGetValue(TemplateOptions.Reasoning, out var v) && TemplateOptions.Allowed[TemplateOptions.Reasoning].Contains(v) ? v : null;

    public IReadOnlyList<PermissionControl> DescribeEnforcement(PermissionProfile profile, AgentMode mode)
    {
        var (sandbox, _) = SandboxFor(profile);
        return Enum.GetValues<AgentPermission>().Select(perm =>
        {
            var g = profile.Get(perm);
            var (level, note) = perm switch
            {
                AgentPermission.FileRead => (EnforcementLevel.AgentConfigured, "Codex reads the workspace."),
                AgentPermission.FileCreate or AgentPermission.FileWrite or AgentPermission.FileDelete or AgentPermission.DirectoryCreate =>
                    sandbox == "read-only" ? (EnforcementLevel.UserApprovalRequired, "sandbox_mode read-only: Codex must ask (on-request) to write.")
                    : (EnforcementLevel.AgentConfigured, "sandbox_mode workspace-write: writes allowed inside the workspace only."),
                AgentPermission.ProcessExecution or AgentPermission.Terminal or AgentPermission.Git or AgentPermission.PackageInstall =>
                    (EnforcementLevel.UserApprovalRequired, "approval_policy on-request: Codex asks before commands that need more than the sandbox allows."),
                AgentPermission.Network => (EnforcementLevel.AgentConfigured, "The Codex sandbox blocks network access from commands by default."),
                AgentPermission.OutsideWorkspace => (EnforcementLevel.AgentConfigured, "Sandbox limits writes to the workspace."),
                AgentPermission.Credentials => (EnforcementLevel.ApplicationEnforced, "No stored credentials are passed to sessions; credential folders are refused as working folders."),
                AgentPermission.LocalServices => (EnforcementLevel.AgentConfigured, "Uses the local Ollama provider only (local mode)."),
                AgentPermission.ExternalApis => (mode == AgentMode.LocalProvider ? EnforcementLevel.AgentConfigured : EnforcementLevel.AdvisoryOnly,
                    mode == AgentMode.LocalProvider ? "Inference via local Ollama." : "Native mode sends prompts to OpenAI by design."),
                AgentPermission.AddOns => (EnforcementLevel.AdvisoryOnly, "MCP servers configured inside Codex are not managed by this app (none in the isolated profile)."),
                AgentPermission.PrivilegedOperations => (EnforcementLevel.ApplicationEnforced, "Sessions are never elevated."),
                _ => (EnforcementLevel.AdvisoryOnly, ""),
            };
            return new PermissionControl(perm, g, level, note);
        }).ToList();
    }
}
