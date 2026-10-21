using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Processes;
using DarkCipher.AgentControl.Domain.Runtimes;

namespace DarkCipher.AgentControl.Integrations.Ollama;

/// <summary>
/// Starts an installed Ollama the way the user would: its own tray app ("ollama app.exe" next to ollama.exe), which runs
/// the server on 127.0.0.1:11434. Launched through the process executor as a background helper, so it keeps running when
/// this app closes. Nothing is installed, configured or exposed beyond Ollama's own defaults, and a running Ollama is
/// never restarted.
/// </summary>
public sealed class OllamaLauncher
{
    public const string TrayAppName = "ollama app.exe";
    private readonly IProcessExecutor _processes;
    private readonly OllamaDetector _detector;
    private readonly OllamaRuntime _runtime;

    public OllamaLauncher(IProcessExecutor processes, OllamaDetector detector, OllamaRuntime runtime)
    {
        _processes = processes;
        _detector = detector;
        _runtime = runtime;
    }

    private static bool Running(RuntimeStatus s) => s.Health is ComponentHealth.Healthy or ComponentHealth.Degraded;

    /// <summary>Waits for the server to answer, polling once a second.</summary>
    public async Task<RuntimeStatus> WaitForServerAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var s = await _runtime.GetStatusAsync(ct).ConfigureAwait(false);
            if (Running(s) || DateTime.UtcNow >= until) return s;
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    public async Task<OperationResult<RuntimeStatus>> EnsureRunningAsync(CancellationToken ct)
    {
        var status = await _runtime.GetStatusAsync(ct).ConfigureAwait(false);
        if (Running(status)) return OperationResult<RuntimeStatus>.Ok(status, "Ollama is already running.");

        var exe = _detector.FindInstallation();
        if (exe is null) return OperationResult<RuntimeStatus>.Fail("Ollama is not installed.");
        var tray = Path.Combine(Path.GetDirectoryName(exe)!, TrayAppName);
        if (!File.Exists(tray)) return OperationResult<RuntimeStatus>.Fail($"Ollama is installed but its app ({TrayAppName}) was not found next to ollama.exe. Start Ollama yourself, then press Retry.");

        var started = _processes.Start(new ProcessSpec { Executable = tray, Mode = ProcessMode.Background, Purpose = "Start Ollama (tray app)" });
        if (!started.Success) return OperationResult<RuntimeStatus>.Fail("Ollama could not be started: " + started.Message);

        status = await WaitForServerAsync(TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        return Running(status)
            ? OperationResult<RuntimeStatus>.Ok(status, "Ollama started.")
            : OperationResult<RuntimeStatus>.Fail("Ollama was started but its server did not answer within 45 seconds. Check the Ollama tray icon, then press Retry.");
    }
}
