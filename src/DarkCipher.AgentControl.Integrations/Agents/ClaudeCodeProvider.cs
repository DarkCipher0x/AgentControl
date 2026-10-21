using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Integrations.Common;

namespace DarkCipher.AgentControl.Integrations.Agents;

/// <summary>
/// Claude Code (Anthropic's CLI). Native mode launches it unchanged against Anthropic. The local-gateway modes use the
/// documented environment variables (ANTHROPIC_BASE_URL, ANTHROPIC_AUTH_TOKEN="ollama", ANTHROPIC_API_KEY="") to point it
/// at Ollama's Anthropic-compatible endpoint. Anthropic does not support that configuration; it is labelled Experimental
/// unless the gateway compatibility test passed for the chosen model, and even then it is never presented as Claude.
/// Only the native installer's claude.exe is launched; npm's claude.cmd shim is refused (batch-file quoting is unsafe).
/// </summary>
public sealed class ClaudeCodeProvider : IAgentProvider, IAgentEndToEndProbe
{
    public const string ProviderId = "claude-code";
    private readonly IProcessExecutor _processes;

    public ClaudeCodeProvider(IProcessExecutor processes) => _processes = processes;

    public string Id => ProviderId;
    public string DisplayName => "Claude Code";
    public string Vendor => "Anthropic";
    public FeatureMaturity Maturity => FeatureMaturity.Supported;

    public IReadOnlyList<AgentModeInfo> Modes { get; } =
    [
        new(AgentMode.Native, "Claude (native)", FeatureMaturity.Supported, AgentProtocol.ProviderNative,
            "Claude Code talks to Anthropic's API with your own Claude login. Prompts and code are sent to Anthropic."),
        new(AgentMode.VerifiedLocalGateway, "Local model via verified gateway", FeatureMaturity.Experimental, AgentProtocol.AnthropicMessages,
            "Claude Code talks to a local model through Ollama's Anthropic-compatible API; the gateway passed this app's text and tool-use tests for the model. Not supported by Anthropic."),
        new(AgentMode.ExperimentalLocalGateway, "Local model via gateway (untested)", FeatureMaturity.Experimental, AgentProtocol.AnthropicMessages,
            "As above, but the gateway has not been tested with this model yet. Expect tool failures with weaker models."),
    ];

    public static IEnumerable<string> CandidatePaths()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, ".local", "bin", "claude.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "claude", "claude.exe");
        // winget's portable install (Anthropic.ClaudeCode): the running app does not see winget's later PATH change.
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "claude.exe");
        // winget package folder (no Links alias on some PCs).
        foreach (var p in WingetPackages.Candidates("Anthropic.ClaudeCode", "claude.exe")) yield return p;
    }

    private readonly DetectionCache _cache = new();

    public void InvalidateDetection() => _cache.Invalidate();

    public Task<AgentInstallation> DetectAsync(CancellationToken ct) => _cache.GetAsync(DetectCoreAsync, ct);

    private async Task<AgentInstallation> DetectCoreAsync(CancellationToken ct)
    {
        var notes = new List<string>();
        var exe = ExecutableLocator.Find("claude.exe", CandidatePaths());
        if (exe is null)
        {
            var shim = ExecutableLocator.FindShimOnPath("claude");
            if (shim is not null) notes.Add($"Found an npm/script shim at {shim}. It is not launched (batch files cannot be quoted safely); install the native Claude Code build instead.");
            return new AgentInstallation(Id, false, null, null, ComponentHealth.Unavailable, "Claude Code is not installed.", notes);
        }

        var r = await _processes.RunAsync(new ProcessSpec { Executable = exe, Arguments = ["--version"], Timeout = TimeSpan.FromSeconds(20), Purpose = "Detect Claude Code version" }, ct).ConfigureAwait(false);
        if (r.Outcome == ProcessOutcome.Blocked)
            return new AgentInstallation(Id, true, exe, null, ComponentHealth.SecurityBlock, r.Message, notes);
        var version = r.Succeeded ? r.StdOut.Split('\n').FirstOrDefault()?.Trim() : null;
        return new AgentInstallation(Id, true, exe, version, r.Succeeded ? ComponentHealth.Healthy : ComponentHealth.Degraded,
            r.Succeeded ? $"Claude Code {version}" : "Claude Code is installed but did not report a version: " + r.Message, notes);
    }

    public AgentLaunchPlan BuildLaunchPlan(AgentLaunchRequest request)
    {
        if (!request.Installation.Installed || request.Installation.ExecutablePath is null)
            throw new InvalidOperationException("Claude Code is not installed.");

        var args = new List<string>();
        var env = new Dictionary<string, string>();
        var clear = new List<string>();
        var warnings = new List<string>();
        string backend;
        FeatureMaturity maturity;

        args.AddRange(PermissionArguments(request.Permissions));

        if (request.Mode == AgentMode.Native)
        {
            backend = "Anthropic API (Claude) — prompts and code leave this PC";
            maturity = FeatureMaturity.Supported;
        }
        else if (request.Mode is AgentMode.VerifiedLocalGateway or AgentMode.ExperimentalLocalGateway)
        {
            var model = ModelNameRules.Require(request.Model);
            var endpoint = request.Endpoint ?? new Uri("http://127.0.0.1:11434");
            if (!endpoint.IsLoopback) throw new InvalidOperationException("The local gateway must be on this PC (loopback). Remote gateways are not supported for Claude Code.");
            env["ANTHROPIC_BASE_URL"] = endpoint.GetLeftPart(UriPartial.Authority);
            env["ANTHROPIC_AUTH_TOKEN"] = "ollama"; // documented placeholder value, not a secret
            clear.Add("ANTHROPIC_API_KEY");
            env["ANTHROPIC_MODEL"] = model;
            env["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model;
            env["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model;
            env["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = model;
            env["CLAUDE_CODE_SUBAGENT_MODEL"] = model;
            env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            env["API_TIMEOUT_MS"] = "1200000";
            // Claude Code assumes a 200K window for models it does not know; give it the real one so auto-compact works.
            if (request.ContextLength is { } ctxTokens and > 0) env["CLAUDE_CODE_MAX_CONTEXT_TOKENS"] = ctxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
            args.Add("--model");
            args.Add(model);
            backend = $"Local model {model} via Ollama Anthropic-compatible API at {env["ANTHROPIC_BASE_URL"]} — NOT Claude";
            maturity = FeatureMaturity.Experimental;
            warnings.Add("Anthropic does not support routing Claude Code to non-Claude models. This is an experimental configuration.");
            if (request.ContextLength is < 65536) warnings.Add($"Context {request.ContextLength:N0} is below the 64K that Ollama recommends for Claude Code.");
            if (request.Mode == AgentMode.ExperimentalLocalGateway) warnings.Add("The gateway has not passed the tool-use test with this model yet. Run Test first.");
        }
        else throw new InvalidOperationException($"Claude Code does not support the {request.Mode} mode.");

        // Template instructions use Claude Code's documented --append-system-prompt (validated, size-limited text).
        if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
        {
            var text = request.SystemInstructions.Trim();
            if (text.Length > TemplateOptions.MaxSystemInstructions || text.Any(c => c < 0x20 && c is not '\n' and not '\t'))
                throw new ArgumentException("The template's instructions are too long or contain control characters.");
            args.Add("--append-system-prompt");
            args.Add(text);
        }
        if (request.Options is { } o && o.ContainsKey(TemplateOptions.Reasoning))
            warnings.Add("The template's reasoning level is not passed to Claude Code (no supported launch option).");

        var spec = new ProcessSpec
        {
            Executable = request.Installation.ExecutablePath,
            Arguments = args,
            WorkingDirectory = request.ProjectDirectory,
            Environment = env,
            ClearEnvironment = clear,
            Mode = ProcessMode.InteractiveConsole,
            Purpose = $"Claude Code session ({request.Mode})",
            Actor = PathActor.Agent,
        };
        return new AgentLaunchPlan(spec, request.Mode, backend, maturity, warnings, [], DescribeEnforcement(request.Permissions, request.Mode));
    }

    /// <summary>Headless end-to-end probe: the normal launch plan plus Claude Code's documented "-p" print mode, captured
    /// (no window). The same approach the opt-in live test proved against the real binary.</summary>
    public EndToEndProbe BuildProbe(AgentLaunchRequest request, string expectedToken)
    {
        var plan = BuildLaunchPlan(request);
        var spec = plan.Process with
        {
            Mode = ProcessMode.Captured,
            Arguments = [.. plan.Process.Arguments, "-p", $"Reply with exactly the word {expectedToken} and nothing else."],
            Timeout = TimeSpan.FromMinutes(5),
            Purpose = "End-to-end verification (Claude Code, headless print mode)",
        };
        return new EndToEndProbe(spec, expectedToken, "Claude Code answers a fixed prompt in print mode (-p) in a disposable project.");
    }

    /// <summary>Maps a permission profile to Claude Code's documented CLI flags. bypassPermissions is never used.</summary>
    internal static IEnumerable<string> PermissionArguments(PermissionProfile p)
    {
        var mode = p.Id switch
        {
            BuiltInPermissionProfiles.ReadOnlyId => "plan",
            BuiltInPermissionProfiles.AskBeforeChangesId => "default",
            _ when p.Get(AgentPermission.FileWrite) == PermissionGrant.Allow => "acceptEdits",
            _ when p.Get(AgentPermission.FileWrite) == PermissionGrant.Deny => "plan",
            _ => "default",
        };
        yield return "--permission-mode";
        yield return mode;

        var deny = new List<string>();
        if (p.Get(AgentPermission.ProcessExecution) == PermissionGrant.Deny || p.Get(AgentPermission.Terminal) == PermissionGrant.Deny) deny.Add("Bash");
        if (p.Get(AgentPermission.Network) == PermissionGrant.Deny) { deny.Add("WebFetch"); deny.Add("WebSearch"); }
        if (p.Get(AgentPermission.FileWrite) == PermissionGrant.Deny) { deny.Add("Edit"); deny.Add("Write"); deny.Add("NotebookEdit"); }
        if (deny.Count > 0)
        {
            yield return "--disallowedTools";
            yield return string.Join(",", deny);
        }
    }

    private static bool BashDenied(PermissionProfile p) =>
        p.Get(AgentPermission.ProcessExecution) == PermissionGrant.Deny || p.Get(AgentPermission.Terminal) == PermissionGrant.Deny;

    public IReadOnlyList<PermissionControl> DescribeEnforcement(PermissionProfile profile, AgentMode mode)
    {
        var list = new List<PermissionControl>();
        foreach (var perm in Enum.GetValues<AgentPermission>())
        {
            var g = profile.Get(perm);
            var (level, note) = perm switch
            {
                AgentPermission.FileRead => (EnforcementLevel.AgentConfigured, "Claude Code reads files in the project; protected folders are refused at launch by this app."),
                AgentPermission.FileCreate or AgentPermission.FileWrite or AgentPermission.DirectoryCreate =>
                    g == PermissionGrant.Ask ? (EnforcementLevel.UserApprovalRequired, "Claude Code asks before each edit (--permission-mode default).")
                    : (EnforcementLevel.AgentConfigured, g == PermissionGrant.Allow ? "--permission-mode acceptEdits" : "--permission-mode plan and Edit/Write disallowed"),
                AgentPermission.FileDelete => (EnforcementLevel.UserApprovalRequired, "Deletion happens through shell commands, which Claude Code asks about unless allowed."),
                AgentPermission.ProcessExecution or AgentPermission.Terminal =>
                    g == PermissionGrant.Deny ? (EnforcementLevel.AgentConfigured, "Bash tool disallowed (--disallowedTools Bash).")
                    : (EnforcementLevel.UserApprovalRequired, "Claude Code asks before running commands it has not been allowed."),
                AgentPermission.Git or AgentPermission.PackageInstall when BashDenied(profile) =>
                    (EnforcementLevel.AgentConfigured, "No shell at all (Bash disallowed), so git and package commands cannot run."),
                AgentPermission.Git or AgentPermission.PackageInstall when g == PermissionGrant.Deny =>
                    (EnforcementLevel.AdvisoryOnly, "Cannot be blocked separately: these run as shell commands, which Claude Code asks about before running."),
                AgentPermission.Git or AgentPermission.PackageInstall =>
                    (EnforcementLevel.UserApprovalRequired, "Run as shell commands; Claude Code asks before running commands it has not been allowed."),
                AgentPermission.Network => g == PermissionGrant.Deny ? (EnforcementLevel.AgentConfigured, "WebFetch/WebSearch disallowed; shell commands could still reach the network.")
                    : (EnforcementLevel.UserApprovalRequired, "Claude Code asks before fetching URLs."),
                AgentPermission.OutsideWorkspace => (EnforcementLevel.AgentConfigured, "Claude Code limits edits to the working folder unless more folders are added; this app never adds any."),
                AgentPermission.Credentials => (EnforcementLevel.ApplicationEnforced, "This app never passes stored credentials to agent sessions; credential folders are blocked as working folders."),
                AgentPermission.LocalServices => (mode == AgentMode.Native ? EnforcementLevel.AdvisoryOnly : EnforcementLevel.ApplicationEnforced,
                    mode == AgentMode.Native ? "Not restricted." : "The gateway endpoint is fixed to loopback by this app."),
                AgentPermission.ExternalApis => (mode == AgentMode.Native ? EnforcementLevel.AdvisoryOnly : EnforcementLevel.AgentConfigured,
                    mode == AgentMode.Native ? "Native mode sends prompts to Anthropic by design." : "Non-essential traffic disabled (CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1)."),
                AgentPermission.AddOns => (EnforcementLevel.AdvisoryOnly, "MCP servers/plugins configured in Claude Code itself are not managed by this app."),
                AgentPermission.PrivilegedOperations => (EnforcementLevel.ApplicationEnforced, "Sessions are started without elevation; this app never runs agents as administrator."),
                _ => (EnforcementLevel.AdvisoryOnly, ""),
            };
            list.Add(new PermissionControl(perm, g, level, note));
        }
        return list;
    }
}
