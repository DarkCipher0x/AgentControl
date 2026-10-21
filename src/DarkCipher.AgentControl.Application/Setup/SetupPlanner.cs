using DarkCipher.AgentControl.Application.Recommendations;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Hardware;
using DarkCipher.AgentControl.Domain.Runtimes;

namespace DarkCipher.AgentControl.Application.Setup;

public enum PlannedOperationKind { InstallRuntime, StartRuntime, InstallAgent, DownloadModel, CreateStack, ConfigureAgentProfile, CreateTestProject, RunCompatibilityTest, OfferSession }

public enum PlannedOperationState { Pending, Skipped, Running, Done, Failed, ManualActionRequired }

/// <summary>One step of the "Get My Local Coding AI Ready" plan. Every step is shown before anything runs.</summary>
public sealed record PlannedOperation(
    PlannedOperationKind Kind,
    string Title,
    string Detail,
    bool RequiresApproval,
    bool RequiresElevation,
    long? DownloadBytes,
    string? Location,
    string? Source,
    PlannedOperationState State = PlannedOperationState.Pending,
    string? ToolId = null);

public sealed record SetupPlan(
    string Name,
    IReadOnlyList<PlannedOperation> Operations,
    long TotalDownloadBytes,
    long? FreeDiskBytes,
    IReadOnlyList<string> ConfigChanges,
    IReadOnlyList<string> Warnings);

public enum OneClickPlan { Lightweight, Balanced, HighQuality, CpuOnly, Hybrid, Advanced }

/// <summary>
/// Builds the onboarding plan from real detection results. Missing runtimes and agents are installed automatically
/// through the catalogue (winget, exact package id, per-user, no administrator rights), each after its own approval
/// dialog that shows the full install plan. Nothing is installed silently. An installed but stopped Ollama is started
/// with its own tray app. Without winget (<c>automaticInstalls</c> false) the plan falls back to ManualActionRequired
/// steps that link to the official source. Models are pulled through the runtime API after approval, and agent
/// configuration is written only to isolated, app-managed profiles.
/// </summary>
public static class SetupPlanner
{
    public const string OllamaToolId = "ollama";

    public static string AgentDisplayName(string agentId) => agentId == "codex" ? "Codex CLI" : "Claude Code";

    public static string WingetId(string toolId) => toolId switch
    {
        "codex" => "OpenAI.Codex",
        "claude-code" => "Anthropic.ClaudeCode",
        _ => "Ollama.Ollama",
    };

    public static RecommendationProfile ProfileFor(OneClickPlan plan) => plan switch
    {
        OneClickPlan.Lightweight => RecommendationProfile.FastAndLightweight,
        OneClickPlan.HighQuality => RecommendationProfile.BestQualityForMyHardware,
        OneClickPlan.CpuOnly => RecommendationProfile.CpuFriendly,
        OneClickPlan.Advanced => RecommendationProfile.Experimental,
        _ => RecommendationProfile.AgenticCoding,
    };

    public static SetupPlan Build(OneClickPlan plan, HardwareProfile hw, RuntimeStatus ollama, IReadOnlyList<AgentInstallation> agents,
        Recommendation? model, string preferredAgentId, long? freeDiskBytes, string profilesDirectory, string testProjectsDirectory,
        bool automaticInstalls = true)
    {
        var ops = new List<PlannedOperation>();
        var warnings = new List<string>();
        var config = new List<string>();
        var localStack = plan != OneClickPlan.Hybrid;
        const string winget = "winget (Windows Package Manager), per-user, no administrator rights";

        if (ollama.InstallLocation is null && ollama.Health == ComponentHealth.Unavailable)
        {
            if (!localStack)
                warnings.Add("Ollama is not installed. Hybrid does not need it.");
            else if (automaticInstalls)
                ops.Add(new(PlannedOperationKind.InstallRuntime, "Install Ollama",
                    "Installed automatically with winget (Ollama.Ollama) after you approve its install plan. Ollama's installer starts it, and it runs on 127.0.0.1:11434.",
                    true, false, 2L * 1000 * 1000 * 1000, @"%LOCALAPPDATA%\Programs\Ollama", winget, ToolId: OllamaToolId));
            else
                ops.Add(new(PlannedOperationKind.InstallRuntime, "Install Ollama", "winget is not available, so Ollama cannot be installed automatically. Install it from ollama.com, then press Retry.",
                    true, false, null, null, "https://ollama.com/download", PlannedOperationState.ManualActionRequired));
        }
        else if (ollama.Health is ComponentHealth.Unavailable)
        {
            if (localStack)
                ops.Add(new(PlannedOperationKind.StartRuntime, "Start Ollama",
                    "Ollama is installed but not running. It is started automatically with its own app, as if you opened it from the Start menu. It keeps running after this app closes.",
                    false, false, null, ollama.InstallLocation, null, ToolId: OllamaToolId));
        }
        else
            ops.Add(new(PlannedOperationKind.StartRuntime, "Ollama is running", ollama.Summary, false, false, null, ollama.Endpoint?.BaseUri.ToString(), null, PlannedOperationState.Skipped));

        var agent = agents.FirstOrDefault(a => a.ProviderId == preferredAgentId);
        var agentName = AgentDisplayName(preferredAgentId);
        if (agent is null || !agent.Installed)
            ops.Add(automaticInstalls
                ? new(PlannedOperationKind.InstallAgent, $"Install {agentName}",
                    $"Installed automatically with winget ({WingetId(preferredAgentId)}) after you approve its install plan, then tested.",
                    true, false, null, @"%LOCALAPPDATA%\Microsoft\WinGet\Packages", winget, ToolId: preferredAgentId)
                : new(PlannedOperationKind.InstallAgent, $"Install {agentName}",
                    $"winget is not available, so {agentName} cannot be installed automatically. Install it from the official instructions, then press Retry.",
                    true, false, null, null, preferredAgentId == "codex" ? "https://developers.openai.com/codex" : "https://docs.anthropic.com/claude-code",
                    PlannedOperationState.ManualActionRequired));
        else
            ops.Add(new(PlannedOperationKind.InstallAgent, $"{agentName} is installed", agent.Summary, false, false, null, agent.ExecutablePath, null, PlannedOperationState.Skipped));

        if (model is null)
            warnings.Add(plan == OneClickPlan.Hybrid ? "Hybrid uses a cloud agent; no local model is required." : "No model fits this hardware for the chosen plan.");
        else if (model.Installed)
            ops.Add(new(PlannedOperationKind.DownloadModel, $"Use installed model {model.Model.OllamaName}", model.Reason, false, false, null, null, null, PlannedOperationState.Skipped));
        else
        {
            ops.Add(new(PlannedOperationKind.DownloadModel, $"Download {model.Model.DisplayName}", model.Reason + " Ollama verifies the download.",
                true, false, model.Model.ApproxSizeBytes, "Ollama model store", "Ollama library (registry.ollama.ai)"));
            if (freeDiskBytes is { } free && free < model.Model.ApproxSizeBytes * 11 / 10)
                warnings.Add($"Free disk space ({Bytes.Format(free)}) may be too small for {Bytes.Format(model.Model.ApproxSizeBytes)}.");
        }

        if (model is not null || plan == OneClickPlan.Hybrid)
        {
            ops.Add(new(PlannedOperationKind.CreateStack, "Create your stack", $"Agent {preferredAgentId}, model {model?.Model.OllamaName ?? "(cloud)"}, permissions: Ask Before Changes.",
                false, false, null, "App settings", null));
            if (preferredAgentId == "codex")
            {
                var path = Path.Combine(profilesDirectory, "<stack>", "config.toml");
                config.Add($"Create isolated Codex profile {path} (your ~/.codex is not touched).");
                ops.Add(new(PlannedOperationKind.ConfigureAgentProfile, "Prepare isolated Codex profile", "Written at launch to the app's own folder.", false, false, null, path, null));
            }
            else config.Add("No files are changed for Claude Code; settings are passed per session as environment variables and flags.");
            ops.Add(new(PlannedOperationKind.CreateTestProject, "Create a disposable test project", "A tiny sample project for the end-to-end test.", false, false, null, testProjectsDirectory, null));
            if (model is not null)
                ops.Add(new(PlannedOperationKind.RunCompatibilityTest, "Run compatibility tests", "Text, instructions, code, JSON schema and tool-call tests against the model (fixed prompts, local only).",
                    false, false, null, null, null));
            ops.Add(new(PlannedOperationKind.OfferSession, "Ready to code", "Offer to start your first session in the test project.", true, false, null, null, null));
        }

        var total = ops.Where(o => o.State == PlannedOperationState.Pending).Sum(o => o.DownloadBytes ?? 0);
        return new SetupPlan(plan.ToString(), ops, total, freeDiskBytes, config, warnings);
    }
}
