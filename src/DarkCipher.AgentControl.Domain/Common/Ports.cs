namespace DarkCipher.AgentControl.Domain.Common;

public enum LogLevel { Trace, Debug, Information, Warning, Error, Critical }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Component, string Message, string? Correlation, string? Exception);

/// <summary>Port: structured application log. Implementations redact; callers must still never pass source code,
/// prompts, model responses or credentials.</summary>
public interface IAppLogger
{
    void Log(LogLevel level, string component, string message, CorrelationId? correlation = null, Exception? exception = null);
    IReadOnlyList<LogEntry> Recent(int max = 2000);
    event EventHandler<LogEntry>? Logged;
}

public static class AppLoggerExtensions
{
    public static void Info(this IAppLogger log, string component, string message, CorrelationId? c = null) => log.Log(LogLevel.Information, component, message, c);
    public static void Warn(this IAppLogger log, string component, string message, CorrelationId? c = null) => log.Log(LogLevel.Warning, component, message, c);
    public static void Error(this IAppLogger log, string component, string message, Exception? ex = null, CorrelationId? c = null) => log.Log(LogLevel.Error, component, message, c, ex);
}

/// <summary>Port: typed, versioned, atomically-written application documents (stacks, projects, settings).</summary>
public interface IDocumentStore
{
    T Load<T>(string name, Func<T> createDefault) where T : class;
    void Save<T>(string name, T value) where T : class;
    /// <summary>Messages about recovered/quarantined documents since start (no silent failures).</summary>
    IReadOnlyList<string> RecoveryNotices { get; }
}

/// <summary>Port: well-known application directories. None may resolve under a protected development tree.</summary>
public interface IAppPaths
{
    string DataRoot { get; }
    string ConfigDirectory { get; }
    string LogDirectory { get; }
    string AuditDirectory { get; }
    string BackupDirectory { get; }
    string ExtensionsDirectory { get; }
    string StagingDirectory { get; }
    string AgentProfilesDirectory { get; }
    string TestProjectsDirectory { get; }
}
