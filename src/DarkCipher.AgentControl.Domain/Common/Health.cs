namespace DarkCipher.AgentControl.Domain.Common;

/// <summary>Uniform component health used by every service, runtime, agent and extension.</summary>
public enum ComponentHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unavailable,
    Misconfigured,
    PermissionDenied,
    SecurityBlock,
    Crashed,
    Incompatible,
}

/// <summary>How mature a capability is. Shown next to features so nothing is presented as more than it is.</summary>
public enum FeatureMaturity
{
    Supported,
    Experimental,
    Limited,
    Planned,
    Unsupported,
}

/// <summary>A health observation with a human-readable reason.</summary>
public sealed record HealthReport(string Component, ComponentHealth Health, string Summary, string? Detail = null)
{
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Result of an operation that can fail without throwing. Messages are user-safe (already redacted).</summary>
public sealed record OperationResult(bool Success, string Message, string? ErrorCode = null)
{
    public static OperationResult Ok(string message = "OK") => new(true, message);
    public static OperationResult Fail(string message, string? code = null) => new(false, message, code);
}

public sealed record OperationResult<T>(bool Success, T? Value, string Message, string? ErrorCode = null)
{
    public static OperationResult<T> Ok(T value, string message = "OK") => new(true, value, message);
    public static OperationResult<T> Fail(string message, string? code = null) => new(false, default, message, code);
}

/// <summary>Correlation ID that flows through logs and audit events for one user-initiated operation.</summary>
public readonly record struct CorrelationId(string Value)
{
    public static CorrelationId New() => new(Guid.NewGuid().ToString("N")[..12]);
    public override string ToString() => Value;
}

/// <summary>Clock abstraction so time-dependent logic is deterministic in tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Byte formatting shared by every layer (decimal GB, matching what runtimes report).</summary>
public static class Bytes
{
    public const long GiB = 1024L * 1024 * 1024;
    public const long MiB = 1024L * 1024;

    public static string Format(long? bytes)
    {
        if (bytes is null or < 0) return "unknown";
        double b = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (b >= 1000 && i < units.Length - 1) { b /= 1000; i++; }
        return i == 0 ? $"{b:0} {units[i]}" : $"{b:0.0} {units[i]}";
    }

    public static string FormatGiB(long bytes) => $"{bytes / (double)GiB:0.0} GiB";
}
