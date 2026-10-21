namespace DarkCipher.AgentControl.Domain.Sessions;

public sealed record ProjectEntry(string Id, string Name, string Path, DateTimeOffset AddedAt)
{
    public string? DefaultStackId { get; init; }
    public string? Notes { get; init; }
}

public enum SessionState { Starting, Running, Exited, Failed, Stopped }

public sealed record SessionInfo(
    string Id,
    string StackId,
    string StackName,
    string ProjectId,
    string ProjectPath,
    string? TrackedProcessId,
    int? ProcessId,
    DateTimeOffset StartedAt,
    SessionState State,
    string BackendDescription,
    string CommandPreview)
{
    public DateTimeOffset? EndedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? Message { get; init; }
}
