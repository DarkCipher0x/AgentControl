using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Domain.Processes;

public enum ProcessMode
{
    /// <summary>Captured stdout/stderr, no window. For detection, version probes, tests.</summary>
    Captured,
    /// <summary>Own console window for an interactive agent session. Output is not captured.</summary>
    InteractiveConsole,
    /// <summary>A long-running helper the user keeps after this app closes (for example the Ollama tray app). No window,
    /// output is not captured, and it is NOT killed with the app. Application code only (refused for agents).</summary>
    Background,
}

/// <summary>A process launch request. Executable and arguments are always separate; nothing is passed through a shell.</summary>
public sealed record ProcessSpec
{
    public required string Executable { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    /// <summary>Environment variables whose values come from the credential store (name → credential id).</summary>
    public IReadOnlyDictionary<string, string> SecretEnvironment { get; init; } = new Dictionary<string, string>();
    /// <summary>Variables explicitly set to empty (e.g. ANTHROPIC_API_KEY="") or removed from the inherited block.</summary>
    public IReadOnlyList<string> ClearEnvironment { get; init; } = [];
    public ProcessMode Mode { get; init; } = ProcessMode.Captured;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public string Purpose { get; init; } = "unspecified";
    /// <summary>When set, a second launch with the same key is refused while the first is alive.</summary>
    public string? DuplicateKey { get; init; }
    public int MaxOutputChars { get; init; } = 256 * 1024;
    public string? StandardInput { get; init; }
    /// <summary>Set ONLY by application services after IApprovalService returned true for a working directory whose
    /// protected-path decision was RequiresApproval. Extensions can never set it (the broker builds specs itself).</summary>
    public bool PathApprovalGranted { get; init; }
    /// <summary>Whose rules apply to the working directory: an agent session is evaluated as <see cref="PathActor.Agent"/>.</summary>
    public PathActor Actor { get; init; } = PathActor.Application;
    /// <summary>Permit a command interpreter (cmd, PowerShell, script hosts). Application code only — ignored for agents and
    /// extensions. Nothing in the app currently sets it.</summary>
    public bool AllowShellInterpreter { get; init; }
}

public enum ProcessOutcome { Completed, Failed, TimedOut, Cancelled, Blocked, StartFailed, Launched }

public sealed record ProcessResult(
    ProcessOutcome Outcome,
    int? ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration,
    string Message,
    int? ProcessId = null,
    bool OutputTruncated = false)
{
    public bool Succeeded => Outcome == ProcessOutcome.Completed && ExitCode == 0;
}

public sealed record TrackedProcess(string Id, int ProcessId, string Executable, string Purpose, DateTimeOffset StartedAt, ProcessMode Mode)
{
    public bool HasExited { get; init; }
    public int? ExitCode { get; init; }
}

public sealed class ProcessBlockedException(string message) : Exception(message);

/// <summary>Port: the ONLY way any component starts a process. Implemented by SecureProcessExecutionService.</summary>
public interface IProcessExecutor
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);

    /// <summary>Starts a long-running process the app owns; returns immediately.</summary>
    OperationResultWithProcess Start(ProcessSpec spec);

    IReadOnlyList<TrackedProcess> Tracked { get; }

    /// <summary>Stops a process this app started (whole job tree). Never kills by name, never touches foreign processes.</summary>
    bool Stop(string trackedId);

    event EventHandler<TrackedProcess>? ProcessExited;

    /// <summary>A redacted, copyable command line for display (secrets replaced).</summary>
    string Preview(ProcessSpec spec);
}

public sealed record OperationResultWithProcess(bool Success, string Message, TrackedProcess? Process);
