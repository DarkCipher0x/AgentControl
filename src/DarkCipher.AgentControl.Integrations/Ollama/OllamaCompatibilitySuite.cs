using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DarkCipher.AgentControl.Domain.Compatibility;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Runtimes;

namespace DarkCipher.AgentControl.Integrations.Ollama;

/// <summary>
/// Runs real tests against a model through the Ollama API. Prompts are fixed fixtures (no user code or data is sent).
/// Tests run against loopback only; the results record model digest, runtime version, suite version and hardware so they
/// can be compared later. Token rates come only from Ollama's own eval counters in these runs, never estimates.
/// Verdict: generation fails → Failed; generation works but tool calls or structured output fail → "Text Generation
/// Available / Agentic Compatibility Failed"; everything required passes → Agentic Compatible.
/// </summary>
public sealed class OllamaCompatibilitySuite : ICompatibilitySuite
{
    public const string SuiteVersion = "1.1.0";
    string ICompatibilitySuite.SuiteVersion => SuiteVersion;
    public string RuntimeId => "ollama";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(3);
    private readonly OllamaRuntime _runtime;

    public OllamaCompatibilitySuite(OllamaRuntime runtime) => _runtime = runtime;

    public async Task<CompatibilityReport> RunAsync(string model, string hardwareSummary, int requiredContext, IProgress<string>? progress, CancellationToken ct)
    {
        ModelNameRules.Require(model);
        var client = _runtime.Client;
        var results = new List<CompatibilityTestResult>();
        string? version = null, digest = null;
        double? tps = null, ttft = null;
        JsonNode? think = null; // reasoning models: keep thinking short so the answer fits the test budget
        JsonObject B(JsonObject message, int maxTokens)
        {
            var b = Body(model, message, maxTokens);
            if (think is not null) b["think"] = think.DeepClone();
            return b;
        }

        async Task Run(string name, bool required, Func<Task<(bool Ok, string Detail)>> test)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(name);
            var sw = Stopwatch.StartNew();
            try
            {
                var (ok, detail) = await test().ConfigureAwait(false);
                results.Add(new CompatibilityTestResult(name, ok, detail, sw.Elapsed, required));
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or JsonException or NetworkBlockedException or InvalidOperationException or KeyNotFoundException)
            {
                results.Add(new CompatibilityTestResult(name, false, ex.Message, sw.Elapsed, required));
            }
        }

        await Run("Connectivity", true, async () =>
        {
            version = await client.VersionAsync(ct).ConfigureAwait(false);
            return (version is not null, version is null ? "Ollama did not answer." : $"Ollama {version}");
        }).ConfigureAwait(false);
        if (version is null) return Report(model, digest, version, hardwareSummary, results, tps, ttft);

        await Run("Model installed", true, async () =>
        {
            var models = await _runtime.ListModelsAsync(ct).ConfigureAwait(false);
            var m = models.FirstOrDefault(x => string.Equals(x.Name, model, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, model + ":latest", StringComparison.OrdinalIgnoreCase));
            digest = m?.Digest;
            if (m?.Family?.StartsWith("gpt-oss", StringComparison.OrdinalIgnoreCase) == true) think = JsonValue.Create("low");
            else if (m?.Supports(Domain.Models.ModelCapabilities.Thinking) == true) think = JsonValue.Create(false);
            return (m is not null, m is null ? "Not installed." : $"{m.ParameterSize} {m.Quantization}");
        }).ConfigureAwait(false);
        if (results[^1].Passed == false) return Report(model, digest, version, hardwareSummary, results, tps, ttft);

        await Run("Load and generate", true, async () =>
        {
            var (resp, chat) = await client.ChatAsync(B(User("Reply with exactly the word READY and nothing else."), 256), TestTimeout, ct).ConfigureAwait(false);
            if (chat?.Message is null) return (false, $"HTTP {resp.StatusCode}: {Trim(resp.Body)}");
            if (chat.EvalCount is > 0 && chat.EvalDuration is > 0) tps = chat.EvalCount.Value / (chat.EvalDuration.Value / 1e9);
            if (chat.LoadDuration is not null && chat.PromptEvalDuration is not null) ttft = (chat.LoadDuration.Value + chat.PromptEvalDuration.Value) / 1e6;
            var text = chat.Message.Content ?? "";
            return (text.Contains("READY", StringComparison.OrdinalIgnoreCase), $"Answered: {Trim(text)}");
        }).ConfigureAwait(false);
        var generationWorks = results[^1].Passed;

        await Run("Instruction following", true, async () =>
        {
            var (_, chat) = await client.ChatAsync(B(User("What is 6 multiplied by 7? Answer with only the number."), 256), TestTimeout, ct).ConfigureAwait(false);
            var text = (chat?.Message?.Content ?? "").Trim();
            return (text.Contains("42", StringComparison.Ordinal) && text.Length <= 40, $"Answered: {Trim(text)}");
        }).ConfigureAwait(false);

        await Run("Code generation", true, async () =>
        {
            var (_, chat) = await client.ChatAsync(B(User("Write a Python function named add that returns the sum of two arguments a and b. Reply with code only."), 512), TestTimeout, ct).ConfigureAwait(false);
            var text = chat?.Message?.Content ?? "";
            return (text.Contains("def add", StringComparison.Ordinal) && text.Contains("return", StringComparison.Ordinal), text.Contains("def add", StringComparison.Ordinal) ? "Produced a function." : $"Answered: {Trim(text)}");
        }).ConfigureAwait(false);

        await Run("Structured output (JSON schema)", true, async () =>
        {
            var body = B(User("Return a JSON object describing a tool named hammer that the user owns 3 of, with the fields name and count."), 1024);
            body["format"] = JsonNode.Parse("""{"type":"object","properties":{"name":{"type":"string"},"count":{"type":"integer"}},"required":["name","count"]}""");
            var (_, chat) = await client.ChatAsync(body, TestTimeout, ct).ConfigureAwait(false);
            var text = chat?.Message?.Content ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return (false, "The model returned no answer content within the test budget (only reasoning, or nothing).");
            using var doc = JsonDocument.Parse(text);
            var ok = doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                     && doc.RootElement.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number;
            return (ok, ok ? "Valid JSON matching the schema." : $"JSON did not match: {Trim(text)}");
        }).ConfigureAwait(false);

        await Run("Tool calling", true, async () =>
        {
            var body = B(User("What is the weather in Paris right now? Use the available tool."), 512);
            body["tools"] = JsonNode.Parse("""[{"type":"function","function":{"name":"get_weather","description":"Get current weather for a city","parameters":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}}}]""");
            var (resp, chat) = await client.ChatAsync(body, TestTimeout, ct).ConfigureAwait(false);
            var call = chat?.Message?.ToolCalls?.FirstOrDefault()?.Function;
            if (call is null) return (false, chat is null ? $"HTTP {resp.StatusCode}: {Trim(resp.Body)}" : "The model answered in text instead of calling the tool.");
            var city = call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty("city", out var cityEl) ? cityEl.GetString() : null;
            var ok = call.Name == "get_weather" && city?.Contains("Paris", StringComparison.OrdinalIgnoreCase) == true;
            return (ok, ok ? "Called get_weather(city=Paris)." : $"Called {call.Name} with {call.Arguments}");
        }).ConfigureAwait(false);

        await Run("Multi-turn conversation", false, async () =>
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "user", ["content"] = "Remember this codeword: TEALFOX." },
                    new JsonObject { ["role"] = "assistant", ["content"] = "Understood, I will remember the codeword." },
                    new JsonObject { ["role"] = "user", ["content"] = "What was the codeword? Reply with only the codeword." }),
                ["options"] = new JsonObject { ["num_predict"] = 256, ["temperature"] = 0 },
            };
            if (think is not null) body["think"] = think.DeepClone();
            var (_, chat) = await client.ChatAsync(body, TestTimeout, ct).ConfigureAwait(false);
            var text = chat?.Message?.Content ?? "";
            return (text.Contains("TEALFOX", StringComparison.OrdinalIgnoreCase), $"Answered: {Trim(text)}");
        }).ConfigureAwait(false);

        await Run("Context window", false, async () =>
        {
            var show = await _runtime.ShowAsync(model, ct).ConfigureAwait(false);
            var ctx = show?.ContextLength;
            return (ctx >= requiredContext, ctx is null ? "Context length unknown." : $"Configured/max context {ctx:N0} tokens; agentic coding wants ≥ {requiredContext:N0}.");
        }).ConfigureAwait(false);

        await Run("Cancellation", false, async () =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(400));
            var sw = Stopwatch.StartNew();
            try { await client.ChatAsync(B(User("Count slowly from 1 to 500, one number per line."), 2048), TestTimeout, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            return (sw.Elapsed < TimeSpan.FromSeconds(5), $"Request abandoned after {sw.Elapsed.TotalMilliseconds:0} ms.");
        }).ConfigureAwait(false);

        _ = generationWorks;
        return Report(model, digest, version, hardwareSummary, results, tps, ttft);
    }

    private static CompatibilityReport Report(string model, string? digest, string? version, string hw, List<CompatibilityTestResult> results, double? tps, double? ttft)
    {
        CompatibilityVerdict verdict;
        var gen = results.FirstOrDefault(r => r.Test == "Load and generate");
        if (gen is null || !gen.Passed) verdict = CompatibilityVerdict.Failed;
        else if (results.Where(r => r.Required).All(r => r.Passed)) verdict = CompatibilityVerdict.AgenticCompatible;
        else verdict = CompatibilityVerdict.TextGenerationOnly;
        return new CompatibilityReport(model, digest, OllamaRuntime.RuntimeId, version, SuiteVersion, hw, DateTimeOffset.UtcNow, results, verdict, tps, ttft);
    }

    private static JsonObject User(string text) => new() { ["role"] = "user", ["content"] = text };

    private static JsonObject Body(string model, JsonObject message, int maxTokens) => new()
    {
        ["model"] = model,
        ["messages"] = new JsonArray(message),
        ["options"] = new JsonObject { ["num_predict"] = maxTokens, ["temperature"] = 0 },
    };

    private static string Trim(string s)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= 120 ? s : s[..120] + "…";
    }
}
