using System.Net;
using System.Net.NetworkInformation;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Downloads;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Domain.Tools;
using DarkCipher.AgentControl.Integrations.Common;

namespace DarkCipher.AgentControl.Integrations.Tools;

/// <summary>
/// A reviewed, built-in catalogue entry for a tool installed with winget. Package ids, licences and per-user installer
/// availability were checked against the winget source on 2026-10-20 (packages without a per-user installer, such as
/// GPT4All and Docker Desktop, were left out because this app never asks for administrator rights).
/// </summary>
public sealed record WingetCatalogueEntry(
    string Id,
    string DisplayName,
    string Publisher,
    string Description,
    ToolKind Kind,
    string PackageId,
    string Homepage,
    string Licence,
    string InstallMethod,
    long EstimatedDiskBytes,
    IReadOnlyList<string> Commands)
{
    public string Authentication { get; init; } = "None";
    public IReadOnlyList<string> Protocols { get; init; } = [];
    public string SupportedModels { get; init; } = "";
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public IReadOnlyList<string> AffectedLocations { get; init; } = [];
    public IReadOnlyList<string> EnvironmentChanges { get; init; } = [];
    public IReadOnlyList<string> ServicesStarted { get; init; } = [];
    public IReadOnlyList<string> ConfigurationChanges { get; init; } = [];
    /// <summary>Extra full paths (with %VARS%) where the main executable may live (installer-based apps).</summary>
    public IReadOnlyList<string> KnownPaths { get; init; } = [];
    /// <summary>Arguments that print the version, or null for GUI apps (running them would open a window).</summary>
    public IReadOnlyList<string>? VersionArguments { get; init; } = ["--version"];
    public SecurityClassification Security { get; init; } = SecurityClassification.Medium;
    public int? LocalPort { get; init; }
}

/// <summary>
/// Install, update, repair, uninstall and detection for a <see cref="WingetCatalogueEntry"/>. Detection reads the shared
/// winget snapshot first (one winget call for the whole catalogue), then looks for the executable (winget links, the
/// package folder, known install folders, PATH) so tools installed outside winget are recognised too.
/// </summary>
public sealed class CatalogueWingetToolProvider : WingetToolProvider
{
    private readonly WingetCatalogueEntry _e;
    private readonly IProcessExecutor _processes;

    public CatalogueWingetToolProvider(WingetClient winget, IProcessExecutor processes, WingetCatalogueEntry entry) : base(winget)
    {
        if (!WingetClient.IsValidPackageId(entry.PackageId)) throw new ArgumentException($"'{entry.PackageId}' is not a valid winget package id.");
        _e = entry;
        _processes = processes;
        var catalogueOnly = entry.Kind is ToolKind.AgentTool or ToolKind.ModelRuntime;
        Descriptor = new AgentToolDescriptor
        {
            Id = entry.Id,
            DisplayName = entry.DisplayName,
            Publisher = entry.Publisher,
            Description = entry.Description,
            Kind = entry.Kind,
            Source = new PackageSource("winget", entry.PackageId, "winget", entry.Publisher, entry.Homepage, entry.Licence),
            InstallMethod = entry.InstallMethod,
            Authentication = entry.Authentication,
            SupportedProtocols = entry.Protocols,
            SupportedModels = entry.SupportedModels,
            KnownLimitations = catalogueOnly
                ? [.. entry.Limitations, "Catalogue only: this app installs, updates, detects and removes it, but no stack or session provider uses it yet."]
                : entry.Limitations,
            Security = entry.Security,
            EstimatedDiskBytes = entry.EstimatedDiskBytes,
            AffectedLocations = entry.AffectedLocations.Count > 0 ? entry.AffectedLocations : [WingetPaths.PackagesDisplay + @"\" + entry.PackageId + "_*"],
            EnvironmentChanges = entry.EnvironmentChanges,
            ServicesStarted = entry.ServicesStarted,
            ConfigurationChanges = entry.ConfigurationChanges,
            CatalogueOnly = catalogueOnly,
            LocalPort = entry.LocalPort,
        };
    }

    public override AgentToolDescriptor Descriptor { get; }

    /// <summary>Where the main executable may be, most specific first.</summary>
    public IEnumerable<string> CandidatePaths()
    {
        foreach (var p in _e.KnownPaths) yield return Environment.ExpandEnvironmentVariables(p);
        foreach (var c in _e.Commands) yield return Path.Combine(WingetPaths.Links, c);
        foreach (var p in WingetPackages.Candidates(_e.PackageId, [.. _e.Commands])) yield return p;
    }

    public string? FindExecutable()
    {
        foreach (var p in CandidatePaths())
            if (File.Exists(p)) return Path.GetFullPath(p);
        // PATH hits under WindowsApps are app-execution aliases (python.exe there is a Microsoft Store stub, not Python).
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps") + Path.DirectorySeparatorChar;
        foreach (var c in _e.Commands)
            if (ExecutableLocator.FindOnPath(c) is { } onPath && !onPath.StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase)) return onPath;
        return null;
    }

    public override async Task<ToolStatus> DetectAsync(CancellationToken ct)
    {
        var version = await Winget.InstalledVersionFromSnapshotAsync(_e.PackageId, ct).ConfigureAwait(false);
        var exe = FindExecutable();
        if (version is not null)
            return new ToolStatus(_e.Id, ToolInstallState.Installed, version, exe, Auth, ComponentHealth.Healthy,
                $"{_e.DisplayName} {version} (winget)" + (exe is null ? "" : $" at {exe}"));
        if (exe is null)
            return new ToolStatus(_e.Id, ToolInstallState.NotInstalled, null, null, Auth, ComponentHealth.Unavailable, $"{_e.DisplayName} is not installed.");

        // Installed outside winget: read the version only for command-line tools (GUI apps would open a window).
        string? found = null;
        if (_e.VersionArguments is { } va)
        {
            var r = await _processes.RunAsync(new ProcessSpec { Executable = exe, Arguments = va, Timeout = TimeSpan.FromSeconds(15), Purpose = $"Detect {_e.DisplayName} version" }, ct).ConfigureAwait(false);
            if (r.Succeeded) found = VersionText.Parse(r.StdOut + " " + r.StdErr)?.ToString(3);
        }
        return new ToolStatus(_e.Id, ToolInstallState.Installed, found, exe, Auth, ComponentHealth.Healthy,
            $"Found at {exe} (installed outside winget{(found is null ? "; version not read" : "")}).");
    }

    private ToolAuthState Auth => _e.Authentication.StartsWith("None", StringComparison.Ordinal) ? ToolAuthState.NotRequired : ToolAuthState.Unknown;
}

/// <summary>Whether something is listening on a loopback TCP port (cheap; no connection is made).</summary>
public static class LoopbackPorts
{
    public static Func<IPEndPoint[]> Listeners { get; set; } = () => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();

    public static bool IsListening(int port)
    {
        try { return Listeners().Any(l => l.Port == port && (IPAddress.IsLoopback(l.Address) || l.Address.Equals(IPAddress.Any) || l.Address.Equals(IPAddress.IPv6Any))); }
        catch (NetworkInformationException) { return false; }
    }
}

/// <summary>
/// A gateway (API server) that ships inside a runtime: LM Studio's local server, llama.cpp's llama-server, Jan's local
/// API, Ollama's OpenAI-compatible API. It is never installed separately: Install plans the parent runtime's install,
/// and update and uninstall are managed on the runtime. Detection: the runtime is installed, and the gateway counts as
/// running when its loopback port is listening. Whether a model actually works through it is a separate test.
/// </summary>
public sealed class RuntimeGatewayToolProvider : IAgentToolProvider
{
    private readonly IAgentToolProvider _parent;

    public RuntimeGatewayToolProvider(IAgentToolProvider parent, string id, string displayName, string description, int port, IReadOnlyList<string> protocols, string howToStart)
    {
        _parent = parent;
        HowToStart = howToStart;
        Descriptor = new AgentToolDescriptor
        {
            Id = id,
            DisplayName = displayName,
            Publisher = parent.Descriptor.Publisher,
            Description = description,
            Kind = ToolKind.Gateway,
            InstallMethod = $"Part of {parent.Descriptor.DisplayName}: installing it installs {parent.Descriptor.DisplayName}.",
            RequiredRuntimes = [parent.Descriptor.DisplayName],
            Authentication = "None (loopback only).",
            SupportedProtocols = protocols,
            SupportedModels = $"Models loaded in {parent.Descriptor.DisplayName}.",
            KnownLimitations = [$"Not used by stacks yet; connect agents that support custom endpoints to http://127.0.0.1:{port}.", "How to start: " + howToStart],
            Security = SecurityClassification.Low,
            CanInstall = parent.Descriptor.CanInstall,
            CanUpdate = false,
            CanUninstall = false,
            PartOfToolId = parent.Descriptor.Id,
            CatalogueOnly = true,
            LocalPort = port,
            Source = parent.Descriptor.Source,
        };
    }

    public AgentToolDescriptor Descriptor { get; }
    public string HowToStart { get; }

    public async Task<ToolStatus> DetectAsync(CancellationToken ct)
    {
        var parent = await _parent.DetectAsync(ct).ConfigureAwait(false);
        var port = Descriptor.LocalPort!.Value;
        if (!parent.Installed)
            return new ToolStatus(Descriptor.Id, ToolInstallState.NotInstalled, null, null, ToolAuthState.NotRequired, ComponentHealth.Unavailable,
                $"{_parent.Descriptor.DisplayName} is not installed.");
        var listening = LoopbackPorts.IsListening(port);
        return new ToolStatus(Descriptor.Id, ToolInstallState.Installed, parent.Version, null, ToolAuthState.NotRequired,
            listening ? ComponentHealth.Healthy : ComponentHealth.Unavailable,
            listening ? $"Listening on 127.0.0.1:{port} (part of {_parent.Descriptor.DisplayName} {parent.Version})." : $"Installed with {_parent.Descriptor.DisplayName}; not running. {HowToStart}");
    }

    public Task<IReadOnlyList<PrerequisiteCheck>> CheckPrerequisitesAsync(CancellationToken ct) => _parent.CheckPrerequisitesAsync(ct);
    public Task<string?> GetAvailableVersionAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Install plans the parent runtime (the plan says so); other operations belong to the runtime.</summary>
    public async Task<InstallPlan> PlanAsync(InstallOperation operation, CancellationToken ct)
    {
        if (operation != InstallOperation.Install)
            throw new InvalidOperationException($"{Descriptor.DisplayName} is part of {_parent.Descriptor.DisplayName}. Update or remove {_parent.Descriptor.DisplayName} instead.");
        var plan = await _parent.PlanAsync(operation, ct).ConfigureAwait(false);
        return plan with { Warnings = [$"{Descriptor.DisplayName} is part of {_parent.Descriptor.DisplayName}, so this installs {_parent.Descriptor.DisplayName}.", .. plan.Warnings] };
    }

    public Task<InstallOutcome> ExecuteAsync(InstallPlan plan, IProgress<DownloadProgress> progress, CancellationToken ct) => _parent.ExecuteAsync(plan, progress, ct);
}

/// <summary>The providers for <see cref="BuiltInCatalogue"/>: one per entry, then the gateways bound to their runtimes.</summary>
public sealed class BuiltInCatalogueSet
{
    public BuiltInCatalogueSet(WingetClient winget, IProcessExecutor processes, IAgentToolProvider ollama)
    {
        var tools = BuiltInCatalogue.Entries.Select(e => (IAgentToolProvider)new CatalogueWingetToolProvider(winget, processes, e)).ToList();
        IAgentToolProvider Parent(string id) => id == ollama.Descriptor.Id ? ollama : tools.Single(t => t.Descriptor.Id == id);
        var gateways = BuiltInCatalogue.Gateways.Select(g => (IAgentToolProvider)new RuntimeGatewayToolProvider(Parent(g.Parent), g.Id, g.Name, g.Description, g.Port, g.Protocols, g.HowToStart));
        All = [.. tools, .. gateways];
    }

    public static int Count => BuiltInCatalogue.Entries.Count + BuiltInCatalogue.Gateways.Count;
    public IReadOnlyList<IAgentToolProvider> All { get; }
}

/// <summary>The reviewed built-in catalogue beyond Claude Code, Codex and Ollama (facts dated 2026-10-20).</summary>
public static class BuiltInCatalogue
{
    private const long MB = 1000L * 1000;
    private const string Portable = "winget (Windows Package Manager), per-user portable install";
    private const string LinksPath = "User-level PATH: winget adds " + WingetPaths.LinksDisplay + " if it is not already there. Nothing system-wide.";

    public static IReadOnlyList<WingetCatalogueEntry> Entries { get; } =
    [
        // ---- Model runtimes ----
        new("lmstudio", "LM Studio", "Element Labs", "Desktop app to download and run local models (GGUF and MLX) with a built-in OpenAI-compatible server and the lms command-line tool.",
            ToolKind.ModelRuntime, "ElementLabs.LMStudio", "https://lmstudio.ai/", "Freeware (LM Studio terms of use)", "winget (Windows Package Manager), per-user installer", 1500 * MB, ["lms.exe"])
        {
            Protocols = ["OpenAI Chat Completions (local server)", "LM Studio REST"],
            SupportedModels = "GGUF models from Hugging Face and the LM Studio catalogue.",
            KnownPaths = [@"%LOCALAPPDATA%\Programs\LM Studio\LM Studio.exe", @"%LOCALAPPDATA%\Programs\lm-studio\LM Studio.exe", @"%USERPROFILE%\.lmstudio\bin\lms.exe"],
            AffectedLocations = [@"%LOCALAPPDATA%\Programs\LM Studio", @"%USERPROFILE%\.lmstudio (models and settings, created on first use)"],
            VersionArguments = null,
            LocalPort = 1234,
            Limitations = ["Its server listens on 127.0.0.1:1234 only after you start it in LM Studio (Developer → Start server) or with 'lms server start'."],
        },
        new("llamacpp", "llama.cpp", "ggml", "The reference C/C++ inference engine: llama-server (OpenAI-compatible HTTP server), llama-cli and the GGUF tools.",
            ToolKind.ModelRuntime, "ggml.llamacpp", "https://github.com/ggml-org/llama.cpp", "MIT", Portable, 200 * MB, ["llama-server.exe", "llama-cli.exe"])
        {
            Protocols = ["OpenAI Chat Completions (llama-server)"],
            SupportedModels = "Any GGUF file you point it at.",
            EnvironmentChanges = [LinksPath],
            LocalPort = 8080,
            Limitations = ["The GPU backend depends on the build winget ships; check 'llama-server --version'.", "You choose and download GGUF files yourself; llama-server serves one model per process."],
        },
        new("jan", "Jan", "Jan","Open-source desktop app for local models with an OpenAI-compatible local API server.",
            ToolKind.ModelRuntime, "Jan.Jan", "https://jan.ai/", "AGPL-3.0", "winget (Windows Package Manager), per-user installer", 800 * MB, ["Jan.exe"])
        {
            Protocols = ["OpenAI Chat Completions (local API server)"],
            SupportedModels = "GGUF models from the Jan hub and Hugging Face; remote providers optional.",
            KnownPaths = [@"%LOCALAPPDATA%\Programs\Jan\Jan.exe", @"%LOCALAPPDATA%\Programs\jan\Jan.exe"],
            AffectedLocations = [@"%LOCALAPPDATA%\Programs\Jan", @"%APPDATA%\Jan (data, created on first use)"],
            VersionArguments = null,
            LocalPort = 1337,
            Limitations = ["Its local API server listens on 127.0.0.1:1337 only after you enable it in Jan's settings."],
        },
        new("llamafile", "llamafile", "Mozilla", "Single-file executable that runs a model (or any GGUF file) with a built-in OpenAI-compatible server.",
            ToolKind.ModelRuntime, "Mozilla.llamafile", "https://github.com/mozilla-ai/llamafile", "Apache-2.0", Portable, 50 * MB, ["llamafile.exe"])
        {
            Protocols = ["OpenAI Chat Completions"],
            SupportedModels = "GGUF files you supply.",
            EnvironmentChanges = [LinksPath],
            LocalPort = 8080,
        },

        // ---- Agent tools ----
        new("copilot-cli", "GitHub Copilot CLI", "GitHub", "GitHub's agentic coding assistant for the terminal, using your Copilot subscription.",
            ToolKind.AgentTool, "GitHub.Copilot", "https://github.com/github/copilot-cli", "Proprietary (GitHub terms)", Portable, 150 * MB, ["copilot.exe"])
        {
            Authentication = "GitHub account with a Copilot plan, asked for on first launch.",
            Protocols = ["Provider native (GitHub Copilot)"],
            SupportedModels = "Models offered by your Copilot plan (cloud).",
            EnvironmentChanges = [LinksPath],
            Limitations = ["Cloud: prompts and code are sent to GitHub."],
        },
        new("opencode", "OpenCode", "SST", "Open-source terminal coding agent that works with many providers, including local OpenAI-compatible servers (Ollama, LM Studio, llama.cpp).",
            ToolKind.AgentTool, "SST.opencode", "https://opencode.ai/", "MIT", Portable, 120 * MB, ["opencode.exe"])
        {
            Authentication = "Depends on the provider you configure; none for local servers.",
            Protocols = ["OpenAI Chat Completions", "Anthropic Messages", "Provider native"],
            SupportedModels = "Cloud providers and local OpenAI-compatible endpoints.",
            EnvironmentChanges = [LinksPath],
            ConfigurationChanges = [@"OpenCode creates %USERPROFILE%\.config\opencode when it first runs (not at install)."],
        },
        new("crush", "Crush", "Charmbracelet", "Terminal AI coding assistant with multiple providers and local OpenAI-compatible endpoints.",
            ToolKind.AgentTool, "charmbracelet.crush", "https://charm.sh/crush", "FSL-1.1-MIT", Portable, 60 * MB, ["crush.exe"])
        {
            Authentication = "Depends on the provider you configure; none for local servers.",
            Protocols = ["OpenAI Chat Completions", "Anthropic Messages"],
            SupportedModels = "Cloud providers and local OpenAI-compatible endpoints.",
            EnvironmentChanges = [LinksPath],
        },
        new("kilo", "Kilo CLI", "Kilo Code", "The standalone command-line agent of the open-source Kilo Code platform.",
            ToolKind.AgentTool, "KiloCode.Kilo", "https://github.com/Kilo-Org/kilocode", "MIT", Portable, 150 * MB, ["kilo.exe"])
        {
            Authentication = "Kilo account or your own provider keys, asked for on first launch.",
            Protocols = ["Provider native", "OpenAI Chat Completions"],
            SupportedModels = "Cloud providers; local OpenAI-compatible endpoints where configured.",
            EnvironmentChanges = [LinksPath],
        },
        new("amp", "Amp CLI", "Sourcegraph", "Sourcegraph's agentic coding tool for the terminal.",
            ToolKind.AgentTool, "Sourcegraph.Amp", "https://ampcode.com/", "Proprietary (Sourcegraph terms)", Portable, 120 * MB, ["amp.exe"])
        {
            Authentication = "Amp account, asked for on first launch.",
            Protocols = ["Provider native"],
            SupportedModels = "Models chosen by Amp (cloud).",
            EnvironmentChanges = [LinksPath],
            Limitations = ["Cloud: prompts and code are sent to Sourcegraph."],
        },

        // ---- Supporting tools (what agents call while they work) ----
        new("git", "Git for Windows", "The Git Development Community", "Version control. Most coding agents need Git to read history, make commits and review diffs.",
            ToolKind.SupportingTool, "Git.Git", "https://gitforwindows.org/", "GPL-2.0", "winget (Windows Package Manager), per-user installer", 400 * MB, ["git.exe"])
        {
            KnownPaths = [@"%LOCALAPPDATA%\Programs\Git\cmd\git.exe", @"%ProgramFiles%\Git\cmd\git.exe"],
            AffectedLocations = [@"%LOCALAPPDATA%\Programs\Git"],
            EnvironmentChanges = [@"User-level PATH: the installer adds %LOCALAPPDATA%\Programs\Git\cmd. Nothing system-wide."],
        },
        new("nodejs-lts", "Node.js LTS", "OpenJS Foundation", "JavaScript runtime with npm. Needed by agents and MCP servers distributed through npm.",
            ToolKind.SupportingTool, "OpenJS.NodeJS.LTS", "https://nodejs.org/", "MIT", Portable, 100 * MB, ["node.exe"])
        {
            KnownPaths = [@"%ProgramFiles%\nodejs\node.exe"],
            EnvironmentChanges = [LinksPath],
        },
        new("python", "Python 3.13", "Python Software Foundation", "Python interpreter. Needed by Python projects, scripts and MCP servers written in Python.",
            ToolKind.SupportingTool, "Python.Python.3.13", "https://www.python.org/", "PSF-2.0", "winget (Windows Package Manager), per-user installer", 150 * MB, ["python.exe"])
        {
            KnownPaths = [@"%LOCALAPPDATA%\Programs\Python\Python313\python.exe"],
            AffectedLocations = [@"%LOCALAPPDATA%\Programs\Python\Python313"],
            EnvironmentChanges = [@"User-level PATH: the installer adds %LOCALAPPDATA%\Programs\Python\Python313 and its Scripts folder. Nothing system-wide."],
        },
        new("uv", "uv", "Astral", "Fast Python package and project manager; runs Python tools and MCP servers without a global install.",
            ToolKind.SupportingTool, "astral-sh.uv", "https://docs.astral.sh/uv/", "Apache-2.0 OR MIT", Portable, 40 * MB, ["uv.exe"])
        { EnvironmentChanges = [LinksPath] },
        new("ripgrep", "ripgrep", "Andrew Gallant", "Very fast code search (rg). Several agents use it to search projects.",
            ToolKind.SupportingTool, "BurntSushi.ripgrep.MSVC", "https://github.com/BurntSushi/ripgrep", "MIT OR Unlicense", Portable, 10 * MB, ["rg.exe"])
        { EnvironmentChanges = [LinksPath] },
        new("fd", "fd", "David Peter", "Fast, user-friendly file finder.",
            ToolKind.SupportingTool, "sharkdp.fd", "https://github.com/sharkdp/fd", "Apache-2.0 OR MIT", Portable, 10 * MB, ["fd.exe"])
        { EnvironmentChanges = [LinksPath] },
        new("jq", "jq", "jqlang", "Command-line JSON processor.",
            ToolKind.SupportingTool, "jqlang.jq", "https://jqlang.org/", "MIT", Portable, 5 * MB, ["jq.exe"])
        { EnvironmentChanges = [LinksPath] },
        new("gh", "GitHub CLI", "GitHub", "GitHub from the terminal: pull requests, issues and Actions. Used by agents that open pull requests.",
            ToolKind.SupportingTool, "GitHub.cli", "https://cli.github.com/", "MIT", Portable, 50 * MB, ["gh.exe"])
        {
            Authentication = "GitHub sign-in with 'gh auth login' when you first use it.",
            EnvironmentChanges = [LinksPath],
        },
    ];

    /// <summary>Gateways built into runtimes: (id, parent tool id, name, description, port, protocols, how to start).</summary>
    public static IReadOnlyList<(string Id, string Parent, string Name, string Description, int Port, string[] Protocols, string HowToStart)> Gateways { get; } =
    [
        ("ollama-openai-api", "ollama", "Ollama OpenAI-compatible API", "The /v1/chat/completions endpoint built into Ollama, for agents that speak the OpenAI protocol.",
            11434, ["OpenAI Chat Completions"], "Runs whenever Ollama runs."),
        ("lmstudio-server", "lmstudio", "LM Studio local server", "LM Studio's OpenAI-compatible HTTP server for the model you load in LM Studio.",
            1234, ["OpenAI Chat Completions", "LM Studio REST"], "Start it in LM Studio (Developer → Start server) or run 'lms server start'."),
        ("llamacpp-server", "llamacpp", "llama.cpp llama-server", "llama.cpp's OpenAI-compatible HTTP server for one GGUF model.",
            8080, ["OpenAI Chat Completions"], "Run 'llama-server -m <model.gguf>'."),
        ("jan-server", "jan", "Jan local API server", "Jan's OpenAI-compatible local API for the model you load in Jan.",
            1337, ["OpenAI Chat Completions"], "Enable the local API server in Jan's settings."),
    ];
}
