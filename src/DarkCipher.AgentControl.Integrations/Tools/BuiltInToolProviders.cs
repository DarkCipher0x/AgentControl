using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Agents;
using DarkCipher.AgentControl.Integrations.Ollama;

namespace DarkCipher.AgentControl.Integrations.Tools;

/// <summary>Where winget puts the command links for portable packages (added to the user PATH by winget, once).</summary>
internal static class WingetPaths
{
    public static string Links => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links");
    public const string LinksDisplay = @"%LOCALAPPDATA%\Microsoft\WinGet\Links";
    public const string PackagesDisplay = @"%LOCALAPPDATA%\Microsoft\WinGet\Packages";
}

/// <summary>Claude Code (Anthropic) — winget package Anthropic.ClaudeCode. Catalogue facts dated 2026-10-20.</summary>
public sealed class ClaudeCodeToolProvider(WingetClient winget, ClaudeCodeProvider agent) : WingetToolProvider(winget)
{
    public override AgentToolDescriptor Descriptor { get; } = new()
    {
        Id = ClaudeCodeProvider.ProviderId,
        DisplayName = "Claude Code",
        Publisher = "Anthropic",
        Description = "Anthropic's agentic coding CLI. Native mode uses Claude models with your Claude account; an experimental local-gateway mode is available through Ollama.",
        Kind = ToolKind.AgentTool,
        Source = new PackageSource("winget", "Anthropic.ClaudeCode", "winget", "Anthropic", "https://claude.com/product/claude-code", "Proprietary — Anthropic's terms (shown by the package agreement)"),
        InstallMethod = "winget (Windows Package Manager), per-user portable install",
        Authentication = "Claude account (Pro, Max, Team or Enterprise) or an Anthropic API key — asked for on first launch; not needed for the local-gateway mode.",
        SupportedProtocols = ["Anthropic Messages", "Provider native"],
        SupportedModels = "Claude models (native). Local models only through an Anthropic-compatible gateway — experimental and not supported by Anthropic.",
        KnownLimitations = ["Local models through a gateway are experimental; tool use quality depends on the model."],
        Security = SecurityClassification.Medium,
        LatestVerifiedVersion = "2.1.296",
        MinimumSupportedVersion = "2.1.0",
        EstimatedDiskBytes = 250L * 1000 * 1000,
        AffectedLocations = [WingetPaths.PackagesDisplay + @"\Anthropic.ClaudeCode_*", WingetPaths.LinksDisplay + @"\claude.exe"],
        ConfigurationChanges = [@"Claude Code creates %USERPROFILE%\.claude when it first runs (not at install)."],
        EnvironmentChanges = ["User-level PATH: winget adds " + WingetPaths.LinksDisplay + " if it is not already there. Nothing system-wide."],
    };

    protected override void Invalidate() => agent.InvalidateDetection();

    public override async Task<ToolStatus> DetectAsync(CancellationToken ct) =>
        FromAgent(Descriptor.Id, await agent.DetectAsync(ct).ConfigureAwait(false), ToolAuthState.Unknown);
}

/// <summary>Codex CLI (OpenAI) — winget package OpenAI.Codex. Not yet verified by this app on any PC.</summary>
public sealed class CodexToolProvider(WingetClient winget, CodexProvider agent) : WingetToolProvider(winget)
{
    public override AgentToolDescriptor Descriptor { get; } = new()
    {
        Id = CodexProvider.ProviderId,
        DisplayName = "Codex CLI",
        Publisher = "OpenAI",
        Description = "OpenAI's open-source coding agent. Native mode uses OpenAI models; --oss mode uses a local model through Ollama in an isolated profile.",
        Kind = ToolKind.AgentTool,
        Source = new PackageSource("winget", "OpenAI.Codex", "winget", "OpenAI", "https://developers.openai.com/codex", "Apache-2.0"),
        InstallMethod = "winget (Windows Package Manager), per-user portable install",
        Authentication = "Native mode: ChatGPT sign-in or an OpenAI API key, asked for on first launch. Local (--oss) mode: none.",
        SupportedProtocols = ["OpenAI Responses", "Ollama (OSS provider)"],
        SupportedModels = "OpenAI models (native); Ollama models with tool calling (local mode).",
        KnownLimitations = ["Codex's Windows sandbox is newer than on macOS/Linux; this app enforces only the launch folder.", "Not yet verified by this app on this PC."],
        Security = SecurityClassification.Medium,
        LatestVerifiedVersion = null,
        EstimatedDiskBytes = 80L * 1000 * 1000,
        AffectedLocations = [WingetPaths.PackagesDisplay + @"\OpenAI.Codex_*", WingetPaths.LinksDisplay + @"\codex.exe"],
        ConfigurationChanges = [@"Native mode uses %USERPROFILE%\.codex (created by Codex). This app's local stacks use an isolated profile in its own data folder instead."],
        EnvironmentChanges = ["User-level PATH: winget adds " + WingetPaths.LinksDisplay + " if it is not already there. Nothing system-wide."],
    };

    protected override void Invalidate() => agent.InvalidateDetection();

    public override async Task<ToolStatus> DetectAsync(CancellationToken ct) =>
        FromAgent(Descriptor.Id, await agent.DetectAsync(ct).ConfigureAwait(false), ToolAuthState.Unknown);
}

/// <summary>Ollama — winget package Ollama.Ollama. The runtime behind every local stack.</summary>
public sealed class OllamaToolProvider(WingetClient winget, OllamaDetector detector, IModelRuntime runtime) : WingetToolProvider(winget)
{
    public const string ToolId = "ollama";

    public override AgentToolDescriptor Descriptor { get; } = new()
    {
        Id = ToolId,
        DisplayName = "Ollama",
        Publisher = "Ollama",
        Description = "Local model runtime with a REST API on 127.0.0.1:11434, an OpenAI-compatible API and an Anthropic-compatible API.",
        Kind = ToolKind.ModelRuntime,
        Source = new PackageSource("winget", "Ollama.Ollama", "winget", "Ollama", "https://ollama.com", "MIT"),
        InstallMethod = "winget (Windows Package Manager), per-user installer",
        Authentication = "None (loopback only by default).",
        SupportedProtocols = ["Ollama REST", "OpenAI Chat Completions", "Anthropic Messages (gateway)"],
        SupportedModels = "Ollama library models and compatible GGUF models (including Hugging Face imports).",
        KnownLimitations = ["GPU acceleration depends on your graphics driver; models that do not fit in VRAM run partly on the CPU."],
        Security = SecurityClassification.Medium,
        LatestVerifiedVersion = "0.40.6",
        MinimumSupportedVersion = "0.9.0",
        EstimatedDiskBytes = 2L * 1000 * 1000 * 1000,
        AffectedLocations = [@"%LOCALAPPDATA%\Programs\Ollama", @"%USERPROFILE%\.ollama (models, created on first use)"],
        ServicesStarted = ["Starts the Ollama app, which runs the server on 127.0.0.1:11434, and adds it to start when you sign in (Ollama's own installer behaviour)."],
        EnvironmentChanges = [@"User-level PATH: the installer adds %LOCALAPPDATA%\Programs\Ollama. Nothing system-wide."],
    };

    public override async Task<ToolStatus> DetectAsync(CancellationToken ct)
    {
        var exe = detector.FindInstallation();
        var status = await runtime.GetStatusAsync(ct).ConfigureAwait(false);
        if (exe is null && status.Health is not (ComponentHealth.Healthy or ComponentHealth.Degraded))
            return new ToolStatus(ToolId, ToolInstallState.NotInstalled, null, null, ToolAuthState.NotRequired, ComponentHealth.Unavailable, "Ollama is not installed.");
        var running = status.Health is ComponentHealth.Healthy or ComponentHealth.Degraded;
        return new ToolStatus(ToolId, ToolInstallState.Installed, status.Version, exe ?? status.InstallLocation, ToolAuthState.NotRequired,
            running ? status.Health : ComponentHealth.Degraded, running ? status.Summary : "Installed but the server is not running. Start Ollama.");
    }
}

/// <summary>
/// Ollama's Anthropic-compatible API, used by Claude Code's local-gateway stacks. It is part of Ollama, so it is never
/// installed separately. Detection is cheap (Ollama running = endpoint present); whether it actually WORKS is proven per
/// model by the gateway test (a real /v1/messages call with text and tool use), whose results the readiness checks use.
/// </summary>
public sealed class OllamaGatewayToolProvider(IModelRuntime runtime, IAgentToolProvider? ollama = null) : IAgentToolProvider
{
    public const string ToolId = "ollama-anthropic-gateway";

    public AgentToolDescriptor Descriptor { get; } = new()
    {
        Id = ToolId,
        DisplayName = "Ollama Anthropic-compatible gateway",
        Publisher = "Ollama",
        Description = "The /v1/messages endpoint built into Ollama. Lets Claude Code talk to a local model (experimental; not supported by Anthropic).",
        Kind = ToolKind.Gateway,
        InstallMethod = "Part of Ollama: installing it installs Ollama.",
        RequiredRuntimes = ["Ollama"],
        Authentication = "None (placeholder token, loopback only).",
        SupportedProtocols = ["Anthropic Messages"],
        SupportedModels = "Ollama models with tool calling.",
        KnownLimitations = ["Experimental path; verified per model with the gateway test."],
        Security = SecurityClassification.Low,
        CanInstall = ollama is not null,
        CanUpdate = false,
        CanUninstall = false,
        PartOfToolId = OllamaToolProvider.ToolId,
        LocalPort = 11434,
        Source = ollama?.Descriptor.Source,
    };

    public async Task<ToolStatus> DetectAsync(CancellationToken ct)
    {
        var status = await runtime.GetStatusAsync(ct).ConfigureAwait(false);
        if (status.Health is not (ComponentHealth.Healthy or ComponentHealth.Degraded))
            return new ToolStatus(ToolId, ToolInstallState.NotInstalled, null, null, ToolAuthState.NotRequired, ComponentHealth.Unavailable, "Ollama is not running, so the gateway is unavailable.");
        return new ToolStatus(ToolId, ToolInstallState.Installed, status.Version, null, ToolAuthState.NotRequired, ComponentHealth.Unknown,
            $"Part of Ollama {status.Version}. Not proven until the gateway test passes for a model (Models → Test Claude Code gateway).");
    }

    public Task<IReadOnlyList<PrerequisiteCheck>> CheckPrerequisitesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PrerequisiteCheck>>([new("Ollama", true, "The gateway is part of Ollama.")]);

    public Task<string?> GetAvailableVersionAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Install plans Ollama itself (the plan says so); update and removal belong to Ollama.</summary>
    public async Task<InstallPlan> PlanAsync(InstallOperation operation, CancellationToken ct)
    {
        if (ollama is null || operation != InstallOperation.Install)
            throw new InvalidOperationException("The gateway is built into Ollama. Update or remove Ollama instead.");
        var plan = await ollama.PlanAsync(operation, ct).ConfigureAwait(false);
        return plan with { Warnings = ["The gateway is part of Ollama, so this installs Ollama.", .. plan.Warnings] };
    }

    public Task<InstallOutcome> ExecuteAsync(InstallPlan plan, IProgress<DownloadProgress> progress, CancellationToken ct) =>
        ollama is null ? Task.FromResult(new InstallOutcome(false, "The gateway is built into Ollama.")) : ollama.ExecuteAsync(plan, progress, ct);
}

/// <summary>
/// A tool the user registered. Never installed by the app; "validation" runs its version command through the secure
/// executor (full path, .exe only, no shell, not in Temp/Downloads, protected-path rules apply) and requires a version.
/// </summary>
public sealed class CustomAgentToolProvider(CustomAgentDefinition definition, IProcessExecutor processes) : IAgentToolProvider
{
    public CustomAgentDefinition Definition => definition;

    public AgentToolDescriptor Descriptor => new()
    {
        Id = definition.Id,
        DisplayName = definition.DisplayName,
        Publisher = "You (custom tool)",
        Description = "A tool you registered. The app does not install, update or vouch for it.",
        Kind = ToolKind.AgentTool,
        Trust = definition.Trust,
        InstallMethod = "Registered by you; installed outside this app.",
        Authentication = definition.AuthenticationMethod,
        SupportedProtocols = definition.SupportedProtocols,
        SupportedModels = string.Join(", ", definition.CompatibleModels),
        KnownLimitations = ["Permission profiles are advisory for custom tools: only the launch folder is enforced by this app."],
        Security = SecurityClassification.High,
        LatestVerifiedVersion = definition.ValidatedVersion,
        CanInstall = false, CanUpdate = false, CanUninstall = false,
    };

    public async Task<ToolStatus> DetectAsync(CancellationToken ct)
    {
        if (!File.Exists(definition.Executable))
            return new ToolStatus(definition.Id, ToolInstallState.NotInstalled, null, null, ToolAuthState.Unknown, ComponentHealth.Unavailable, "The executable does not exist.");
        var r = await processes.RunAsync(new ProcessSpec
        {
            Executable = definition.Executable,
            Arguments = definition.VersionArguments,
            Timeout = TimeSpan.FromSeconds(20),
            Purpose = $"Validate custom tool {definition.DisplayName}",
        }, ct).ConfigureAwait(false);
        if (r.Outcome == ProcessOutcome.Blocked)
            return new ToolStatus(definition.Id, ToolInstallState.Broken, null, definition.Executable, ToolAuthState.Unknown, ComponentHealth.SecurityBlock, "Blocked: " + r.Message);
        var version = VersionText.Parse(r.StdOut + " " + r.StdErr)?.ToString(3);
        return r.Succeeded && version is not null
            ? new ToolStatus(definition.Id, ToolInstallState.Installed, version, definition.Executable, ToolAuthState.Unknown, ComponentHealth.Healthy, $"{definition.DisplayName} {version}")
            : new ToolStatus(definition.Id, ToolInstallState.Broken, null, definition.Executable, ToolAuthState.Unknown, ComponentHealth.Degraded,
                r.Succeeded ? "The version command did not print a version." : "The version command failed: " + r.Message);
    }

    public Task<IReadOnlyList<PrerequisiteCheck>> CheckPrerequisitesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PrerequisiteCheck>>([]);
    public Task<string?> GetAvailableVersionAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<InstallPlan> PlanAsync(InstallOperation operation, CancellationToken ct) => throw new InvalidOperationException("Custom tools are installed outside this app.");
    public Task<InstallOutcome> ExecuteAsync(InstallPlan plan, IProgress<DownloadProgress> progress, CancellationToken ct) =>
        Task.FromResult(new InstallOutcome(false, "Custom tools are installed outside this app."));
}
