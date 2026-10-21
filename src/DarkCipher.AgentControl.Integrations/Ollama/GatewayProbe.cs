using System.Text.Json;
using System.Text.Json.Nodes;
using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Runtimes;

namespace DarkCipher.AgentControl.Integrations.Ollama;

/// <summary>Facts about a local protocol gateway, shown in full before any agent is pointed at it.</summary>
public sealed record GatewayInfo(
    string Name,
    string? Version,
    string Source,
    string Licence,
    Uri Address,
    AgentProtocol Protocol,
    string Authentication,
    string? Model,
    ComponentHealth Health,
    CompatibilityVerdict Compatibility,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Tests Ollama's protocol-compatibility endpoints with real requests: the Anthropic Messages endpoint used by Claude
/// Code in local-gateway mode, and the OpenAI Responses endpoint used by Codex. A gateway is "verified" only when text
/// AND tool use both work for the chosen model. Loopback only.
/// </summary>
public sealed class GatewayProbe
{
    private readonly OllamaRuntime _runtime;

    public GatewayProbe(OllamaRuntime runtime) => _runtime = runtime;

    public static IReadOnlyList<string> AnthropicGatewayLimitations =>
    [
        "Anthropic does not support routing Claude Code to non-Claude models; behaviour can change with any Claude Code update.",
        "The model answering is the local model shown here, not Claude. Quality and tool reliability are the local model's.",
        "Prompt caching, extended thinking budgets, web search and some tool features may be missing or behave differently.",
        "Claude Code expects a large context window; Ollama's documentation recommends 64K tokens or more.",
    ];

    public async Task<GatewayInfo> ProbeAnthropicAsync(string model, CancellationToken ct)
    {
        ModelNameRules.Require(model);
        var client = _runtime.Client;
        string? version = null;
        try { version = await client.VersionAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or NetworkBlockedException) { }

        var limitations = AnthropicGatewayLimitations.ToList();
        if (version is null)
            return new GatewayInfo("Ollama Anthropic-compatible API", null, "Ollama (MIT licence)", "MIT", client.BaseUri, AgentProtocol.AnthropicMessages,
                "None (local; token value 'ollama' is a placeholder)", model, ComponentHealth.Unavailable, CompatibilityVerdict.NotRun, limitations);

        var text = await TryAsync(async () =>
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 256,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with exactly the word READY." }),
            };
            var r = await client.PostRawAsync("/v1/messages", body, NetworkPurpose.Inference, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            if (!r.IsSuccess) return (false, $"HTTP {r.StatusCode}");
            using var doc = JsonDocument.Parse(r.Body);
            var ok = doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                     && content.EnumerateArray().Any(b => b.TryGetProperty("type", out var t) && t.GetString() == "text");
            return (ok, ok ? "Text response in Anthropic format." : "Unexpected response shape.");
        }).ConfigureAwait(false);

        var tools = await TryAsync(async () =>
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 512,
                ["tools"] = JsonNode.Parse("""[{"name":"get_weather","description":"Get current weather for a city","input_schema":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}]"""),
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "What is the weather in Paris? Use the tool." }),
            };
            var r = await client.PostRawAsync("/v1/messages", body, NetworkPurpose.Inference, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
            if (!r.IsSuccess) return (false, $"HTTP {r.StatusCode}");
            using var doc = JsonDocument.Parse(r.Body);
            var ok = doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                     && content.EnumerateArray().Any(b => b.TryGetProperty("type", out var t) && t.GetString() == "tool_use");
            return (ok, ok ? "tool_use block returned." : "No tool_use block.");
        }).ConfigureAwait(false);

        if (!text.Ok) limitations.Insert(0, "Text test failed: " + text.Detail);
        if (!tools.Ok) limitations.Insert(0, "Tool-use test failed: " + tools.Detail + " — Claude Code cannot edit files or run commands with this model.");
        var verdict = text.Ok && tools.Ok ? CompatibilityVerdict.AgenticCompatible : text.Ok ? CompatibilityVerdict.TextGenerationOnly : CompatibilityVerdict.Failed;
        return new GatewayInfo("Ollama Anthropic-compatible API", version, "Ollama (official build or AMD AI Bundle build)", "MIT", client.BaseUri,
            AgentProtocol.AnthropicMessages, "None (local; token value 'ollama' is a placeholder)", model,
            text.Ok ? ComponentHealth.Healthy : ComponentHealth.Degraded, verdict, limitations);
    }

    private static async Task<(bool Ok, string Detail)> TryAsync(Func<Task<(bool, string)>> f)
    {
        try { return await f().ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or NetworkBlockedException) { return (false, ex.Message); }
    }
}
