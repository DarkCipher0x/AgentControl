using DarkCipher.AgentControl.Domain.Agents;
using DarkCipher.AgentControl.Domain.Models;

namespace DarkCipher.AgentControl.Domain.Compatibility;

public enum CompatibilityVerdict
{
    NotRun,
    /// <summary>All required agentic tests passed (tool calls, structured output, instructions).</summary>
    AgenticCompatible,
    /// <summary>"Text Generation Available / Agentic Compatibility Failed".</summary>
    TextGenerationOnly,
    Failed,
}

public sealed record CompatibilityTestResult(string Test, bool Passed, string Detail, TimeSpan Duration, bool Required = true);

public sealed record CompatibilityReport(
    string Model,
    string? ModelDigest,
    string RuntimeId,
    string? RuntimeVersion,
    string SuiteVersion,
    string HardwareSummary,
    DateTimeOffset RunAt,
    IReadOnlyList<CompatibilityTestResult> Results,
    CompatibilityVerdict Verdict,
    double? MeasuredTokensPerSecond,
    double? TimeToFirstTokenMs);

/// <summary>One row of the agent compatibility matrix. Result is NotRun until a real test populates it.</summary>
public sealed record AgentMatrixEntry(
    string Agent,
    string Runtime,
    AgentProtocol Protocol,
    string Model,
    ModelCapabilities RequiredCapabilities,
    int RequiredContext,
    CompatibilityVerdict Result,
    string Note);
