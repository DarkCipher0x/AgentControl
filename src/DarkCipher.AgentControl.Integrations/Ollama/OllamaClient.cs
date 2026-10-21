using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DarkCipher.AgentControl.Domain.Network;
using DarkCipher.AgentControl.Domain.Runtimes;

namespace DarkCipher.AgentControl.Integrations.Ollama;

/// <summary>Thin, typed client for the documented Ollama REST API (no CLI scraping). All traffic goes through
/// <see cref="INetworkClient"/>, so privacy mode, endpoint approval and size limits apply.</summary>
public sealed class OllamaClient
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly INetworkClient _net;

    public OllamaClient(INetworkClient net, Uri baseUri)
    {
        _net = net;
        BaseUri = baseUri;
    }

    public Uri BaseUri { get; }

    private Uri U(string path) => new(BaseUri, path);

    public async Task<string?> VersionAsync(CancellationToken ct)
    {
        var r = await _net.SendAsync(new NetworkRequest(HttpMethod.Get, U("/api/version"), NetworkPurpose.RuntimeManagement) { Timeout = TimeSpan.FromSeconds(4) }, ct).ConfigureAwait(false);
        return r.IsSuccess ? JsonSerializer.Deserialize<VersionDto>(r.Body, Json)?.Version : null;
    }

    public async Task<IReadOnlyList<TagModelDto>> TagsAsync(CancellationToken ct)
    {
        var r = await Get("/api/tags", ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TagsDto>(r.Body, Json)?.Models ?? [];
    }

    public async Task<IReadOnlyList<PsModelDto>> PsAsync(CancellationToken ct)
    {
        var r = await Get("/api/ps", ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<PsDto>(r.Body, Json)?.Models ?? [];
    }

    public async Task<ShowDto?> ShowAsync(string model, CancellationToken ct)
    {
        ModelNameRules.Require(model);
        var r = await Post("/api/show", new JsonObject { ["model"] = model }, NetworkPurpose.RuntimeManagement, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        return r.IsSuccess ? JsonSerializer.Deserialize<ShowDto>(r.Body, Json) : null;
    }

    public async Task<NetworkResponse> DeleteAsync(string model, CancellationToken ct)
    {
        ModelNameRules.Require(model);
        return await _net.SendAsync(new NetworkRequest(HttpMethod.Delete, U("/api/delete"), NetworkPurpose.RuntimeManagement)
        { JsonBody = new JsonObject { ["model"] = model }.ToJsonString() }, ct).ConfigureAwait(false);
    }

    public Task<NetworkResponse> CopyAsync(string source, string destination, CancellationToken ct)
    {
        ModelNameRules.Require(source);
        ModelNameRules.Require(destination);
        return Post("/api/copy", new JsonObject { ["source"] = source, ["destination"] = destination }, NetworkPurpose.RuntimeManagement, ct);
    }

    /// <summary>Unloads a model from memory (documented: generate with keep_alive 0 and no prompt).</summary>
    public Task<NetworkResponse> UnloadAsync(string model, CancellationToken ct)
    {
        ModelNameRules.Require(model);
        return Post("/api/generate", new JsonObject { ["model"] = model, ["keep_alive"] = 0 }, NetworkPurpose.RuntimeManagement, ct, TimeSpan.FromSeconds(30));
    }

    public IAsyncEnumerable<string> PullStreamAsync(string model, CancellationToken ct)
    {
        ModelNameRules.Require(model);
        return _net.StreamLinesAsync(new NetworkRequest(HttpMethod.Post, U("/api/pull"), NetworkPurpose.RuntimeManagement)
        { JsonBody = new JsonObject { ["model"] = model, ["stream"] = true }.ToJsonString(), Timeout = TimeSpan.FromSeconds(30) }, ct);
    }

    /// <summary>Non-streaming chat used by compatibility tests. Prompts are test fixtures, never user code.</summary>
    public async Task<(NetworkResponse Response, ChatResponseDto? Parsed)> ChatAsync(JsonObject body, TimeSpan timeout, CancellationToken ct)
    {
        body["stream"] = false;
        var r = await Post("/api/chat", body, NetworkPurpose.Inference, ct, timeout).ConfigureAwait(false);
        ChatResponseDto? parsed = null;
        if (r.IsSuccess) { try { parsed = JsonSerializer.Deserialize<ChatResponseDto>(r.Body, Json); } catch (JsonException) { } }
        return (r, parsed);
    }

    public Task<NetworkResponse> PostRawAsync(string path, JsonObject body, NetworkPurpose purpose, TimeSpan timeout, CancellationToken ct) =>
        Post(path, body, purpose, ct, timeout);

    private async Task<NetworkResponse> Get(string path, CancellationToken ct)
    {
        var r = await _net.SendAsync(new NetworkRequest(HttpMethod.Get, U(path), NetworkPurpose.RuntimeManagement) { Timeout = TimeSpan.FromSeconds(10) }, ct).ConfigureAwait(false);
        if (!r.IsSuccess) throw new HttpRequestException($"Ollama returned HTTP {r.StatusCode} for {path}.");
        return r;
    }

    private Task<NetworkResponse> Post(string path, JsonObject body, NetworkPurpose purpose, CancellationToken ct, TimeSpan? timeout = null) =>
        _net.SendAsync(new NetworkRequest(HttpMethod.Post, U(path), purpose) { JsonBody = body.ToJsonString(), Timeout = timeout ?? TimeSpan.FromSeconds(30) }, ct);

    // ---- DTOs (documented API fields only) ----
    public sealed record VersionDto(string? Version);
    public sealed record TagsDto(List<TagModelDto>? Models);
    public sealed record TagModelDto(string Name, string? Model, long Size, string? Digest,
        [property: JsonPropertyName("modified_at")] DateTimeOffset? ModifiedAt, DetailsDto? Details, List<string>? Capabilities);
    public sealed record DetailsDto(
        string? Format, string? Family, [property: JsonPropertyName("parameter_size")] string? ParameterSize,
        [property: JsonPropertyName("quantization_level")] string? QuantizationLevel,
        [property: JsonPropertyName("context_length")] int? ContextLength,
        [property: JsonPropertyName("parent_model")] string? ParentModel);
    public sealed record PsDto(List<PsModelDto>? Models);
    public sealed record PsModelDto(string Name, long Size, [property: JsonPropertyName("size_vram")] long SizeVram,
        [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt, [property: JsonPropertyName("context_length")] int? ContextLength);
    public sealed record ShowDto(string? License, string? Parameters, DetailsDto? Details, List<string>? Capabilities,
        [property: JsonPropertyName("model_info")] Dictionary<string, JsonElement>? ModelInfo);
    public sealed record ChatMessageDto(string? Role, string? Content, string? Thinking, [property: JsonPropertyName("tool_calls")] List<ToolCallDto>? ToolCalls);
    public sealed record ToolCallDto(ToolFunctionDto? Function);
    public sealed record ToolFunctionDto(string? Name, JsonElement Arguments);
    public sealed record ChatResponseDto(ChatMessageDto? Message, bool Done,
        [property: JsonPropertyName("total_duration")] long? TotalDuration, [property: JsonPropertyName("load_duration")] long? LoadDuration,
        [property: JsonPropertyName("prompt_eval_count")] int? PromptEvalCount, [property: JsonPropertyName("prompt_eval_duration")] long? PromptEvalDuration,
        [property: JsonPropertyName("eval_count")] int? EvalCount, [property: JsonPropertyName("eval_duration")] long? EvalDuration, string? Error);
}
