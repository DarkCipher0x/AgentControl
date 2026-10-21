using DarkCipher.AgentControl.Domain.Stacks;

namespace DarkCipher.AgentControl.Application.Setup;

public sealed record RepairFinding(string Problem, string Cause, string SuggestedAction, bool AppCanFix);

/// <summary>"Repair Stack" — diagnoses first and proposes actions; it changes nothing by itself.</summary>
public static class RepairService
{
    public static IReadOnlyList<RepairFinding> Diagnose(StackReadinessReport report)
    {
        var list = new List<RepairFinding>();
        foreach (var c in report.Checks.Where(c => !c.Passed))
        {
            list.Add(c.Name switch
            {
                "Agent installed" => new(c.Detail, "The agent's executable was not found in its known locations or on PATH.", "Install it from the official source, then press Rescan.", false),
                "Runtime" => new(c.Detail, "The runtime is not answering on its endpoint.", "Start Ollama (or AMD AI Bundle). If it runs, check Security → Network for a block.", false),
                "Primary model" => new(c.Detail, "The stack refers to a model that is not installed.", "Download the model from Models, or change the stack's model.", true),
                "Tool calling" => new(c.Detail, "Agents need tool calls to edit files and run commands.", "Pick a model with tool support (filter Models by 'Tools').", true),
                "Context" => new(c.Detail, "The model's configured context window is smaller than the stack expects.", "Choose a model with a larger context or lower the stack's context.", true),
                "Compatibility" => new(c.Detail, "No passing compatibility result for this exact model.", "Run the compatibility test in Models.", true),
                "Privacy policy" => new(c.Detail, "Policy and stack disagree about cloud use.", "Change the privacy mode in Security, or use a local stack.", false),
                "Agent mode" => new(c.Detail, "The agent does not support the configured mode.", "Edit the stack and choose a supported mode.", true),
                _ => new(c.Detail, "See the check details.", "Review the stack configuration.", false),
            });
        }
        return list;
    }
}
