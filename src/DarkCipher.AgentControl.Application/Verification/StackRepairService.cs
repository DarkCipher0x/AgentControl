using DarkCipher.AgentControl.Domain.Stacks;
using DarkCipher.AgentControl.Domain.Templates;

namespace DarkCipher.AgentControl.Application.Verification;

/// <summary>One proposed repair. Nothing is changed until the user runs the action it names, which has its own plan/approval.</summary>
public sealed record RepairPlan(
    string Issue,
    string Evidence,
    string ProposedCorrection,
    CorrectiveAction Action,
    IReadOnlyList<string> FilesAffected,
    IReadOnlyList<string> ServicesAffected,
    string PrivilegesRequired,
    string Rollback,
    string Validation);

/// <summary>
/// Diagnose-first repair: turns a readiness report into specific, minimal corrections with evidence. It never proposes a
/// blanket reinstall or a reset of the user's configuration; each action routes to its own planned, approved operation.
/// </summary>
public static class StackRepairService
{
    public static IReadOnlyList<RepairPlan> Diagnose(StackProfile stack, StackReadinessReport report) =>
        report.Checks.Where(c => !c.Passed && c.Fix != CorrectiveAction.None).Select(c => Plan(stack, c)).ToList();

    private static RepairPlan Plan(StackProfile s, ReadinessCheck c) => c.Fix switch
    {
        CorrectiveAction.InstallAgent => new(c.Detail, c.Evidence ?? c.Detail, $"Install {s.AgentProviderId} from the catalogue (winget, per-user).", c.Fix,
            [@"%LOCALAPPDATA%\Microsoft\WinGet"], [], "Your user account; no administrator rights.", "Uninstall from the catalogue.", "Version probe after install; readiness re-check."),
        CorrectiveAction.UpdateAgent => new(c.Detail, c.Evidence ?? "", $"Update {s.AgentProviderId} from the catalogue.", c.Fix,
            [@"%LOCALAPPDATA%\Microsoft\WinGet"], [], "Your user account.", "Reinstall the previous version with winget if still offered.", "Version probe; readiness re-check."),
        CorrectiveAction.InstallRuntime => new(c.Detail, c.Evidence ?? "", "Install Ollama from the catalogue.", c.Fix,
            [@"%LOCALAPPDATA%\Programs\Ollama"], ["Ollama server (127.0.0.1:11434), started at sign-in by Ollama's installer"], "Your user account.", "Uninstall from the catalogue.", "Runtime health check."),
        CorrectiveAction.StartRuntime => new(c.Detail, c.Evidence ?? "", "Start Ollama: Fix starts its own app (no administrator rights; it keeps running after this app closes), then re-checks.", c.Fix,
            [], ["Ollama server on 127.0.0.1:11434"], "Your user account.", "Stop it again from Services.", "Runtime health check."),
        CorrectiveAction.PullModel => new(c.Detail, c.Evidence ?? "", $"Pull {s.PrimaryModel} through the download manager (digests verified by Ollama).", c.Fix,
            [@"%USERPROFILE%\.ollama\models"], [], "None.", "Remove the model from Models.", "Runtime lists the model; optional compatibility suite."),
        CorrectiveAction.ChooseCompatibleModel => new(c.Detail, c.Evidence ?? "", "Choose a model that passed the compatibility suite (Templates or Stacks → Edit).", c.Fix,
            ["This stack's profile in the app's data folder"], [], "None.", "Change it back.", "Readiness re-check."),
        CorrectiveAction.ReduceContext => new(c.Detail, c.Evidence ?? "", "Lower the stack's context to what the model and GPU support.", c.Fix,
            ["This stack's profile"], [], "None.", "Change it back.", "Readiness re-check."),
        CorrectiveAction.RunCompatibilityTest => new(c.Detail, c.Evidence ?? "", "Run the compatibility suite for the model (Models → Test compatibility).", c.Fix,
            [], ["Loads the model in Ollama for a few minutes"], "None.", "Not needed (read-only test).", "Suite verdict stored for the model digest."),
        CorrectiveAction.RepairGateway => new(c.Detail, c.Evidence ?? "", "Run the Claude Code gateway test for the model (Models → Test Claude Code gateway).", c.Fix,
            [], ["Ollama"], "None.", "Not needed (read-only test).", "Gateway verdict stored for the model."),
        CorrectiveAction.ChangePrivacyMode => new(c.Detail, c.Evidence ?? "", "Change the privacy mode in Security, or use a local template.", c.Fix,
            ["Security policy"], [], "None.", "Change it back in Security.", "Readiness re-check."),
        CorrectiveAction.ChangePermissionProfile => new(c.Detail, c.Evidence ?? "", "Pick an existing permission profile for this stack.", c.Fix,
            ["This stack's profile"], [], "None.", "Change it back.", "Readiness re-check."),
        CorrectiveAction.ApproveNetworkDestination => new(c.Detail, c.Evidence ?? "", "Approve the endpoint in Security → Network, or use the loopback endpoint.", c.Fix,
            ["Security policy"], [], "None.", "Remove the approval.", "Readiness re-check."),
        CorrectiveAction.RollBackConfiguration => new(c.Detail, c.Evidence ?? "", "Roll back the last configuration change for this stack.", c.Fix,
            ["The agent profile file"], [], "None.", "Re-apply from the stack.", "Re-read and value check."),
        _ => new(c.Detail, c.Evidence ?? "", c.Fix.ToString(), c.Fix, [], [], "None.", "—", "Readiness re-check."),
    };
}
