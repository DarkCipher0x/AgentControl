using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Processes;

namespace DarkCipher.AgentControl.Domain.Compatibility;

/// <summary>Port: the model compatibility suite (basic inference, instruction following, tool calling, structured output).</summary>
public interface ICompatibilitySuite
{
    string RuntimeId { get; }
    string SuiteVersion { get; }
    Task<CompatibilityReport> RunAsync(string model, string hardwareSummary, int requiredContext, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>A safe end-to-end probe: the agent is started headless in a disposable project with read-only permissions,
/// asked to answer with a fixed token, and its output is checked for that token.</summary>
public sealed record EndToEndProbe(ProcessSpec Process, string ExpectedToken, string Description);

/// <summary>Port (optional on an agent provider): builds the end-to-end probe for a launch request.</summary>
public interface IAgentEndToEndProbe
{
    EndToEndProbe BuildProbe(AgentLaunchRequest request, string expectedToken);
}
