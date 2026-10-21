using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Security;
using DarkCipher.AgentControl.Security.Credentials;
using DarkCipher.AgentControl.Security.Native;

namespace DarkCipher.AgentControl.Security.Processes;

/// <summary>
/// The only process launcher in the application.
/// <list type="bullet">
/// <item>Direct executable invocation (UseShellExecute=false), never via cmd/PowerShell; arguments are passed as a list.</item>
/// <item>Executable, working directory and environment are validated (<see cref="ExecutableRules"/>, protected paths,
/// <see cref="EnvironmentRules"/>); code-injection variables are stripped from the inherited block.</item>
/// <item>Every process is placed in its own Job Object so its whole tree can be stopped; captured processes are killed
/// with the app (KILL_ON_JOB_CLOSE). Only processes this service started can be stopped; nothing is killed by name.</item>
/// <item>Output is bounded and redacted; timeouts and cancellation terminate the job; duplicate launches are refused.</item>
/// <item>Every launch, block and stop is audited.</item>
/// </list>
/// Known limitation: a process is assigned to its job just after it starts, so a child spawned in the first instant
/// could escape the job (documented in docs/SECURITY.md).
/// </summary>
public sealed class SecureProcessExecutionService : IProcessExecutor, IDisposable
{
    private readonly IProtectedPathService _paths;
    private readonly IRedactor _redactor;
    private readonly IAuditLogger _audit;
    private readonly SecureCredentialService? _credentials;
    private readonly IAppLogger? _log;
    private readonly ConcurrentDictionary<string, Owned> _owned = new();
    private readonly ConcurrentDictionary<string, string> _duplicateKeys = new(StringComparer.OrdinalIgnoreCase);

    public SecureProcessExecutionService(IProtectedPathService paths, IRedactor redactor, IAuditLogger audit, SecureCredentialService? credentials, IAppLogger? log)
    {
        _paths = paths;
        _redactor = redactor;
        _audit = audit;
        _credentials = credentials;
        _log = log;
    }

    public event EventHandler<TrackedProcess>? ProcessExited;

    private sealed class Owned(TrackedProcess info, Process process, SafeJobHandle job, string? dupKey)
    {
        public TrackedProcess Info { get; set; } = info;
        public Process Process { get; } = process;
        public SafeJobHandle Job { get; } = job;
        public string? DuplicateKey { get; } = dupKey;
    }

    public IReadOnlyList<TrackedProcess> Tracked => _owned.Values.Select(o => o.Info).OrderByDescending(i => i.StartedAt).ToList();

    public string Preview(ProcessSpec spec)
    {
        var sb = new StringBuilder();
        foreach (var kv in spec.Environment.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('=').Append(CommandLine.Quote(kv.Value)).Append(' ');
        foreach (var name in spec.ClearEnvironment) sb.Append(name).Append("=\"\" ");
        sb.Append(CommandLine.Render(spec.Executable, spec.Arguments));
        // Redact everything first; credential placeholders are added afterwards so they stay readable.
        var secrets = string.Concat(spec.SecretEnvironment.OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=<credential> "));
        return secrets + _redactor.Redact(sb.ToString());
    }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        if (spec.Mode != ProcessMode.Captured)
            return new ProcessResult(ProcessOutcome.Blocked, null, "", "", TimeSpan.Zero, "RunAsync only runs captured processes; use Start for sessions.");

        Prepared prepared;
        try { prepared = Prepare(spec); }
        catch (UnsafeProcessException ex) { return Blocked(spec, ex.Message); }

        var sw = Stopwatch.StartNew();
        var psi = prepared.StartInfo;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = spec.StandardInput is not null;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new BoundedBuffer(spec.MaxOutputChars);
        var stderr = new BoundedBuffer(spec.MaxOutputChars);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        using var job = CreateJob(killOnClose: true);
        try
        {
            if (!process.Start()) return Failed(spec, "The program did not start.", sw.Elapsed);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Failed(spec, "The program could not be started: " + _redactor.Redact(ex.Message), sw.Elapsed);
        }
        AssignToJob(job, process);
        Audit("ProcessStarted", AuditResult.Success, spec, SecurityClassification.Low, $"pid {process.Id}, captured");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (spec.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(spec.StandardInput.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeout = new CancellationTokenSource(spec.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var outcome = ProcessOutcome.Completed;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            process.WaitForExit(); // flush async output handlers
        }
        catch (OperationCanceledException)
        {
            outcome = timeout.IsCancellationRequested && !ct.IsCancellationRequested ? ProcessOutcome.TimedOut : ProcessOutcome.Cancelled;
            NativeMethods.TerminateJobObject(job, 1);
            try { process.WaitForExit(2000); } catch (InvalidOperationException) { }
        }

        int? exit = process.HasExited ? process.ExitCode : null;
        if (outcome == ProcessOutcome.Completed && exit != 0) outcome = ProcessOutcome.Failed;
        var message = outcome switch
        {
            ProcessOutcome.Completed => "Completed",
            ProcessOutcome.Failed => $"Exited with code {exit}",
            ProcessOutcome.TimedOut => $"Timed out after {spec.Timeout.TotalSeconds:0} s; the process tree was stopped.",
            _ => "Cancelled; the process tree was stopped.",
        };
        if (outcome is ProcessOutcome.TimedOut or ProcessOutcome.Cancelled)
            Audit("ProcessTerminated", AuditResult.Cancelled, spec, SecurityClassification.Low, message);

        return new ProcessResult(outcome, exit, _redactor.Redact(stdout.ToString()), _redactor.Redact(stderr.ToString()), sw.Elapsed, message,
            process.Id, stdout.Truncated || stderr.Truncated);
    }

    public OperationResultWithProcess Start(ProcessSpec spec)
    {
        Prepared prepared;
        try { prepared = Prepare(spec); }
        catch (UnsafeProcessException ex)
        {
            Blocked(spec, ex.Message);
            return new OperationResultWithProcess(false, ex.Message, null);
        }

        if (spec.DuplicateKey is { } key && _duplicateKeys.ContainsKey(key))
        {
            Audit("ProcessDuplicateRefused", AuditResult.Blocked, spec, SecurityClassification.Low, key);
            return new OperationResultWithProcess(false, "This session is already running. Stop it first or open the running one.", null);
        }

        if (spec.Mode == ProcessMode.Background && spec.Actor != PathActor.Application)
        {
            Blocked(spec, "Only the application itself may start background helpers.");
            return new OperationResultWithProcess(false, "Only the application itself may start background helpers.", null);
        }

        var psi = prepared.StartInfo;
        psi.CreateNoWindow = spec.Mode != ProcessMode.InteractiveConsole;
        var job = CreateJob(killOnClose: spec.Mode == ProcessMode.Captured);
        Process process;
        try
        {
            if (spec.Mode == ProcessMode.Background) process = StartWithoutInheritance(psi, job);
            else
            {
                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (!process.Start()) { job.Dispose(); process.Dispose(); return new OperationResultWithProcess(false, "The program did not start.", null); }
                AssignToJob(job, process);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            job.Dispose();
            var msg = "The program could not be started: " + _redactor.Redact(ex.Message);
            Audit("ProcessStartFailed", AuditResult.Failure, spec, SecurityClassification.Low, msg);
            return new OperationResultWithProcess(false, msg, null);
        }

        var id = Guid.NewGuid().ToString("N")[..12];
        var info = new TrackedProcess(id, process.Id, prepared.Executable, spec.Purpose, DateTimeOffset.UtcNow, spec.Mode);
        var owned = new Owned(info, process, job, spec.DuplicateKey);
        _owned[id] = owned;
        if (spec.DuplicateKey is { } k) _duplicateKeys[k] = id;
        process.Exited += (_, _) => OnExited(id);
        if (process.HasExited) OnExited(id);

        Audit("ProcessStarted", AuditResult.Success, spec, SecurityClassification.Low, $"pid {process.Id}, {spec.Mode}");
        return new OperationResultWithProcess(true, "Started", info);
    }

    public bool Stop(string trackedId)
    {
        if (!_owned.TryGetValue(trackedId, out var owned)) return false; // not ours: never touched
        if (!owned.Info.HasExited)
        {
            NativeMethods.TerminateJobObject(owned.Job, 1);
            _audit.Record(new AuditEvent("ProcessStopped", "SecureProcessExecutionService", AuditResult.Success,
                $"{owned.Info.Executable} (pid {owned.Info.ProcessId})", SecurityClassification.Low, Detail: owned.Info.Purpose));
        }
        return true;
    }

    private void OnExited(string id)
    {
        if (!_owned.TryGetValue(id, out var owned)) return;
        int? code = null;
        try { code = owned.Process.ExitCode; } catch (InvalidOperationException) { }
        lock (owned)
        {
            if (owned.Info.HasExited) return;
            owned.Info = owned.Info with { HasExited = true, ExitCode = code };
        }
        if (owned.DuplicateKey is { } k) _duplicateKeys.TryRemove(k, out _);
        _log?.Info("Processes", $"Process {owned.Info.ProcessId} ({owned.Info.Purpose}) exited with code {code}.");
        ProcessExited?.Invoke(this, owned.Info);
    }

    private sealed record Prepared(ProcessStartInfo StartInfo, string Executable);

    private Prepared Prepare(ProcessSpec spec)
    {
        var exe = ExecutableRules.Validate(spec.Executable, _paths, spec.Actor, spec.AllowShellInterpreter);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, ErrorDialog = false, LoadUserProfile = false };
        var totalArgs = 0;
        foreach (var a in spec.Arguments)
        {
            if (a is null || a.IndexOf('\0') >= 0) throw new UnsafeProcessException("An argument contains a NUL character.");
            totalArgs += a.Length + 3;
            psi.ArgumentList.Add(a);
        }
        if (totalArgs > 30000) throw new UnsafeProcessException("The command line is too long.");

        if (spec.WorkingDirectory is { } wd)
        {
            var decision = _paths.Evaluate(wd, PathOperation.UseAsWorkingDirectory, spec.Actor);
            if (decision.Kind == PathDecisionKind.Deny) throw new UnsafeProcessException(decision.Reason);
            if (decision.Kind == PathDecisionKind.RequiresApproval && !spec.PathApprovalGranted)
                throw new UnsafeProcessException(decision.Reason + " (approval required)");
            if (!Directory.Exists(decision.CanonicalPath)) throw new UnsafeProcessException("The working folder does not exist.");
            psi.WorkingDirectory = decision.CanonicalPath;
        }
        else
        {
            psi.WorkingDirectory = Path.GetDirectoryName(exe)!;
        }

        foreach (var name in EnvironmentRules.CodeInjectionVariables) psi.Environment.Remove(name);
        foreach (var (name, value) in spec.Environment)
        {
            EnvironmentRules.ValidateName(name);
            EnvironmentRules.ValidateValue(name, value);
            psi.Environment[name] = value;
        }
        foreach (var name in spec.ClearEnvironment)
        {
            EnvironmentRules.ValidateName(name);
            psi.Environment[name] = "";
        }
        foreach (var (name, credentialId) in spec.SecretEnvironment)
        {
            EnvironmentRules.ValidateName(name);
            var secret = _credentials?.Reveal(credentialId) ?? throw new UnsafeProcessException($"The credential for '{name}' was not found.");
            _redactor.RegisterSecret(secret);
            psi.Environment[name] = secret;
        }
        return new Prepared(psi, exe);
    }

    private static SafeJobHandle CreateJob(bool killOnClose)
    {
        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new InvalidOperationException("Could not create a job object: " + Marshal.GetLastWin32Error());
        if (killOnClose)
        {
            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            NativeMethods.SetInformationJobObject(job, NativeMethods.JobObjectExtendedLimitInformation, ref info,
                (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        }
        return job;
    }

    /// <summary>
    /// Background helpers outlive this app, so they must not hold any of its handles. .NET always creates processes with
    /// handle inheritance on, which let a helper keep this app's standard-output pipe open (seen live: a test run never
    /// ended while the Ollama tray app was alive). This calls CreateProcessW with bInheritHandles = FALSE, the same
    /// validated executable, quoted arguments, working folder and filtered environment, and no window. The process is
    /// placed in a job (without kill-on-close) so Stop still ends its whole tree.
    /// </summary>
    private Process StartWithoutInheritance(ProcessStartInfo psi, SafeJobHandle job)
    {
        var cmd = new StringBuilder(CommandLine.Render(psi.FileName, psi.ArgumentList.ToList()));
        var env = new StringBuilder();
        foreach (var (k, v) in psi.Environment.Where(e => e.Value is not null).OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)) env.Append(k).Append('=').Append(v).Append('\0');
        env.Append('\0');
        var si = new NativeMethods.STARTUPINFOW { cb = Marshal.SizeOf<NativeMethods.STARTUPINFOW>() };
        if (!NativeMethods.CreateProcessW(psi.FileName, cmd, IntPtr.Zero, IntPtr.Zero, false,
                NativeMethods.CREATE_NO_WINDOW | NativeMethods.CREATE_UNICODE_ENVIRONMENT | NativeMethods.CREATE_NEW_PROCESS_GROUP,
                env.ToString(), psi.WorkingDirectory, ref si, out var pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!NativeMethods.AssignProcessToJobObject(job, pi.hProcess))
                _log?.Warn("Processes", $"Could not place pid {pi.dwProcessId} in a job object (error {Marshal.GetLastWin32Error()}); only the main process can be stopped.");
            var p = Process.GetProcessById(pi.dwProcessId);
            p.EnableRaisingEvents = true;
            return p;
        }
        finally
        {
            NativeMethods.CloseHandle(pi.hThread);
            NativeMethods.CloseHandle(pi.hProcess);
        }
    }

    private void AssignToJob(SafeJobHandle job, Process process)
    {
        try
        {
            if (!NativeMethods.AssignProcessToJobObject(job, process.Handle))
                _log?.Warn("Processes", $"Could not place pid {process.Id} in a job object (error {Marshal.GetLastWin32Error()}); only the main process can be stopped.");
        }
        catch (InvalidOperationException) { /* already exited */ }
    }

    private ProcessResult Blocked(ProcessSpec spec, string reason)
    {
        Audit("ProcessBlocked", AuditResult.Blocked, spec, SecurityClassification.High, reason);
        _log?.Warn("Processes", "Blocked: " + reason);
        return new ProcessResult(ProcessOutcome.Blocked, null, "", "", TimeSpan.Zero, reason);
    }

    private static ProcessResult Failed(ProcessSpec spec, string reason, TimeSpan elapsed) =>
        new(ProcessOutcome.StartFailed, null, "", "", elapsed, reason);

    private void Audit(string action, AuditResult result, ProcessSpec spec, SecurityClassification cls, string detail) =>
        _audit.Record(new AuditEvent(action, "SecureProcessExecutionService", result, spec.Executable, cls, Detail: $"{spec.Purpose}: {detail}"));

    public void Dispose()
    {
        foreach (var o in _owned.Values)
        {
            if (o.Info.Mode == ProcessMode.Captured) NativeMethods.TerminateJobObject(o.Job, 1);
            o.Job.Dispose();
            o.Process.Dispose();
        }
        _owned.Clear();
    }

    /// <summary>Caps captured output so a chatty or hostile process cannot exhaust memory.</summary>
    private sealed class BoundedBuffer(int max)
    {
        private readonly StringBuilder _sb = new();
        public bool Truncated { get; private set; }

        public void AppendLine(string line)
        {
            lock (_sb)
            {
                if (_sb.Length >= max) { Truncated = true; return; }
                var room = max - _sb.Length;
                if (line.Length + 1 > room) { _sb.Append(line.AsSpan(0, Math.Max(0, room - 1))); Truncated = true; }
                else _sb.Append(line).Append('\n');
            }
        }

        public override string ToString() { lock (_sb) return _sb.ToString(); }
    }
}
