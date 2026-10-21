namespace DarkCipher.AgentControl.Domain.Network;

/// <summary>Where a network destination lives. Unknown is never treated as local.</summary>
public enum NetworkClass
{
    Unknown,
    Loopback,
    LocalNetwork,
    PrivateRemote,
    PublicInternet,
}

public enum PrivacyMode
{
    /// <summary>No cloud, no fallback; only loopback and explicitly approved local-network endpoints.</summary>
    FullyLocal,
    LocalPreferred,
    Hybrid,
    CloudAllowed,
}

/// <summary>Why a request is made. Privacy mode rules depend on it: inference never leaves the PC in Fully Local mode.</summary>
public enum NetworkPurpose
{
    /// <summary>Health/version probes and management APIs of a runtime.</summary>
    RuntimeManagement,
    /// <summary>Sending prompts or code to a model.</summary>
    Inference,
    /// <summary>Searching model catalogues and reading model metadata.</summary>
    ModelCatalog,
    ModelDownload,
    UpdateCheck,
}

public sealed record NetworkRequest(HttpMethod Method, Uri Uri, NetworkPurpose Purpose)
{
    public string? JsonBody { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    /// <summary>A credential reference resolved by the network service; the caller never sees the secret.</summary>
    public string? BearerCredentialId { get; init; }
    public TimeSpan? Timeout { get; init; }
    public long MaxResponseBytes { get; init; } = 8 * 1024 * 1024;
}

public sealed record NetworkResponse(int StatusCode, string Body, IReadOnlyDictionary<string, string> Headers)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

public sealed class NetworkBlockedException(string message) : Exception(message);

/// <summary>Port: the ONLY way any component reaches the network. Implemented by the Security assembly.</summary>
public interface INetworkClient
{
    NetworkClass Classify(Uri uri);
    Task<NetworkResponse> SendAsync(NetworkRequest request, CancellationToken ct);

    /// <summary>Streams a response line by line (NDJSON progress streams). Bounded by the same policy.</summary>
    IAsyncEnumerable<string> StreamLinesAsync(NetworkRequest request, CancellationToken ct);
}
