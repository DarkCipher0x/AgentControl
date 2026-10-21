using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Models;
using DarkCipher.AgentControl.Domain.Network;

namespace DarkCipher.AgentControl.Domain.Runtimes;

/// <summary>Connection classification for a runtime endpoint.</summary>
public enum RuntimeConnection { Unknown, Local, Remote, Unavailable }

public enum RuntimeAuth { None, Authenticated, Unauthenticated }

public enum RuntimeTrust { Trusted, Unverified }

public sealed record RuntimeEndpoint(Uri BaseUri, NetworkClass Class, RuntimeAuth Auth, RuntimeTrust Trust);

public sealed record RuntimeStatus(
    string RuntimeId,
    ComponentHealth Health,
    RuntimeConnection Connection,
    string? Version,
    RuntimeEndpoint? Endpoint,
    string? InstallLocation,
    string Summary);

public sealed record PullProgress(string Status, long? Completed, long? Total, string? Digest)
{
    public double? Fraction => Total is > 0 && Completed is not null ? Math.Clamp((double)Completed.Value / Total.Value, 0, 1) : null;
}

/// <summary>Port: a local or remote inference runtime (Ollama today; LM Studio, llama.cpp, vLLM, LocalAI are planned adapters).</summary>
public interface IModelRuntime
{
    string Id { get; }
    string DisplayName { get; }
    FeatureMaturity Maturity { get; }

    Task<RuntimeStatus> GetStatusAsync(CancellationToken ct);
    Task<IReadOnlyList<ModelDescriptor>> ListModelsAsync(CancellationToken ct);
    Task<IReadOnlyList<RunningModel>> ListRunningAsync(CancellationToken ct);
    Task<ModelDescriptor?> ShowAsync(string model, CancellationToken ct);
    Task<OperationResult> PullAsync(string model, IProgress<PullProgress> progress, CancellationToken ct);
    Task<OperationResult> DeleteAsync(string model, CancellationToken ct);
    Task<OperationResult> CopyAsync(string source, string destination, CancellationToken ct);
    Task<OperationResult> UnloadAsync(string model, CancellationToken ct);
}

/// <summary>Validates model names before they reach any runtime API or command line.</summary>
public static class ModelNameRules
{
    // registry/namespace/name:tag and hf.co/user/repo:quant. No whitespace, quotes, shell metacharacters or "..".
    // Character classes are disjoint from the separators, so the backtracking engine runs in linear time here; the
    // timeout is a second line of defence. (Counted repetitions make the non-backtracking automaton too large.)
    private static readonly System.Text.RegularExpressions.Regex Pattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._\-]{0,127}(/[A-Za-z0-9][A-Za-z0-9._\-]{0,127}){0,3}(:[A-Za-z0-9][A-Za-z0-9._\-]{0,127})?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 300 && !name.Contains("..", StringComparison.Ordinal) && Pattern.IsMatch(name);

    public static string Require(string? name) =>
        IsValid(name) ? name! : throw new ArgumentException("Model name is not valid. Use letters, digits, '.', '_', '-', '/', and one ':tag'.");
}
