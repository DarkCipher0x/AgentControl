using DarkCipher.AgentControl.Sdk;

namespace Sample.AgentDetector;

/// <summary>Registers one command. The process request goes through the host broker, which validates the executable,
/// runs it captured (no window), time-limited and audited, and refuses shells and protected locations.</summary>
public sealed class Entry : IExtension
{
    public Task InitializeAsync(IExtensionContext context, CancellationToken cancellationToken)
    {
        var broker = context.Broker;
        broker.RegisterCommand(new SdkCommand("detect-agents", "Detect other agent CLIs", "Looks for aider, gemini and opencode on PATH.", async ct =>
        {
            var where = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
            var r = await broker.RunApprovedProcessAsync(new SdkProcessRequest(where, ["aider", "gemini", "opencode"], null, TimeSpan.FromSeconds(10), "detect agent CLIs"), ct);
            broker.Log(SdkLogLevel.Information, $"Agent detection finished: {r.Message}");
            return r.StdOut.Length > 0 ? r.StdOut : "None of aider, gemini or opencode were found on PATH.";
        }));
        broker.ReportHealth(SdkHealth.Healthy, "Ready");
        return Task.CompletedTask;
    }
}
