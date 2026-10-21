namespace DarkCipher.AgentControl.Domain.Common;

/// <summary>
/// A useful error: what failed, in which component, why (when known), what had already completed safely, what was rolled
/// back, whether user data was touched, what to do next, and a correlation id that matches the log and audit entries.
/// Messages are user-safe (redacted before they get here).
/// </summary>
public sealed record ErrorReport
{
    public required string WhatFailed { get; init; }
    public required string Component { get; init; }
    public string? Why { get; init; }
    public IReadOnlyList<string> CompletedSafely { get; init; } = [];
    public IReadOnlyList<string> RolledBack { get; init; } = [];
    public string UserDataAffected { get; init; } = "No user data was changed.";
    public required string RecommendedAction { get; init; }
    public string CorrelationId { get; init; } = Common.CorrelationId.New().Value;

    public IReadOnlyList<string> Lines()
    {
        var l = new List<string> { "Component: " + Component };
        if (Why is not null) l.Add("Why: " + Why);
        l.AddRange(CompletedSafely.Select(x => "Completed safely: " + x));
        l.AddRange(RolledBack.Select(x => "Rolled back: " + x));
        l.Add("User data: " + UserDataAffected);
        l.Add("Next step: " + RecommendedAction);
        l.Add("Reference: " + CorrelationId + " (search for it in Logs or Security → Audit)");
        return l;
    }

    /// <summary>For an unexpected exception: never "Something went wrong" without the detail beside it.</summary>
    public static ErrorReport FromException(string whatFailed, string component, Exception ex, string? correlation = null) => new()
    {
        WhatFailed = whatFailed,
        Component = component,
        Why = ex.Message,
        RecommendedAction = "Try again. If it keeps failing, open Logs and look for the reference below; the full technical detail is there.",
        CorrelationId = correlation ?? Common.CorrelationId.New().Value,
    };
}
