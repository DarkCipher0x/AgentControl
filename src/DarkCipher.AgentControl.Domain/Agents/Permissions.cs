namespace DarkCipher.AgentControl.Domain.Agents;

public enum AgentPermission
{
    FileRead,
    FileCreate,
    FileWrite,
    FileDelete,
    DirectoryCreate,
    ProcessExecution,
    Terminal,
    Network,
    PackageInstall,
    Git,
    OutsideWorkspace,
    Credentials,
    LocalServices,
    ExternalApis,
    AddOns,
    PrivilegedOperations,
}

public enum PermissionGrant { Deny, Ask, Allow }

/// <summary>Who actually enforces a permission. The UI must show this next to every control.</summary>
public enum EnforcementLevel
{
    /// <summary>DarkCipher AgentControl itself blocks the action (e.g. refuses to launch in a protected folder).</summary>
    ApplicationEnforced,
    /// <summary>Passed to the agent as configuration (flags/config); the agent is responsible for honouring it.</summary>
    AgentConfigured,
    /// <summary>The agent asks the user before acting (agent-side prompt).</summary>
    UserApprovalRequired,
    /// <summary>Shown as guidance only; nothing technically prevents the action.</summary>
    AdvisoryOnly,
    /// <summary>Cannot be expressed for this agent at all.</summary>
    Unsupported,
}

public sealed record PermissionProfile(
    string Id,
    string Name,
    string Description,
    IReadOnlyDictionary<AgentPermission, PermissionGrant> Grants,
    bool BuiltIn)
{
    public PermissionGrant Get(AgentPermission p) => Grants.TryGetValue(p, out var g) ? g : PermissionGrant.Deny;
}

public sealed record PermissionControl(AgentPermission Permission, PermissionGrant Grant, EnforcementLevel Enforcement, string Note);

public static class BuiltInPermissionProfiles
{
    public const string ReadOnlyId = "read-only";
    public const string AskBeforeChangesId = "ask-before-changes";
    public const string WorkspaceOnlyId = "workspace-only";
    public const string StandardId = "standard-development";
    public const string AdvancedId = "advanced-development";

    /// <summary>The safest useful default: the agent may read and propose, every change is approved by the user.</summary>
    public const string DefaultId = AskBeforeChangesId;

    private static Dictionary<AgentPermission, PermissionGrant> All(PermissionGrant g) =>
        Enum.GetValues<AgentPermission>().ToDictionary(p => p, _ => g);

    private static IReadOnlyDictionary<AgentPermission, PermissionGrant> With(PermissionGrant baseline, params (AgentPermission P, PermissionGrant G)[] overrides)
    {
        var d = All(baseline);
        foreach (var (p, g) in overrides) d[p] = g;
        return d;
    }

    private static readonly Lazy<IReadOnlyList<PermissionProfile>> Cached = new(Build);

    /// <summary>The built-in profiles (one shared instance each, so UI selection and equality work).</summary>
    public static IReadOnlyList<PermissionProfile> All() => Cached.Value;

    private static IReadOnlyList<PermissionProfile> Build() =>
    [
        new(ReadOnlyId, "Read-Only", "The agent can read files in the project and answer questions. No changes, no commands.",
            With(PermissionGrant.Deny, (AgentPermission.FileRead, PermissionGrant.Allow)), true),
        new(AskBeforeChangesId, "Ask Before Changes", "The agent reads freely and asks before every edit or command. Recommended default.",
            With(PermissionGrant.Deny,
                (AgentPermission.FileRead, PermissionGrant.Allow), (AgentPermission.FileCreate, PermissionGrant.Ask),
                (AgentPermission.FileWrite, PermissionGrant.Ask), (AgentPermission.FileDelete, PermissionGrant.Ask),
                (AgentPermission.DirectoryCreate, PermissionGrant.Ask), (AgentPermission.ProcessExecution, PermissionGrant.Ask),
                (AgentPermission.Terminal, PermissionGrant.Ask), (AgentPermission.Git, PermissionGrant.Ask),
                (AgentPermission.LocalServices, PermissionGrant.Allow)), true),
        new(WorkspaceOnlyId, "Project Workspace Only", "Edits inside the selected project folder without asking; nothing outside it; commands still ask.",
            With(PermissionGrant.Deny,
                (AgentPermission.FileRead, PermissionGrant.Allow), (AgentPermission.FileCreate, PermissionGrant.Allow),
                (AgentPermission.FileWrite, PermissionGrant.Allow), (AgentPermission.FileDelete, PermissionGrant.Ask),
                (AgentPermission.DirectoryCreate, PermissionGrant.Allow), (AgentPermission.ProcessExecution, PermissionGrant.Ask),
                (AgentPermission.Terminal, PermissionGrant.Ask), (AgentPermission.Git, PermissionGrant.Ask),
                (AgentPermission.LocalServices, PermissionGrant.Allow)), true),
        new(StandardId, "Standard Development", "Edits in the project without asking; commands, git and package installs ask first.",
            With(PermissionGrant.Deny,
                (AgentPermission.FileRead, PermissionGrant.Allow), (AgentPermission.FileCreate, PermissionGrant.Allow),
                (AgentPermission.FileWrite, PermissionGrant.Allow), (AgentPermission.FileDelete, PermissionGrant.Ask),
                (AgentPermission.DirectoryCreate, PermissionGrant.Allow), (AgentPermission.ProcessExecution, PermissionGrant.Ask),
                (AgentPermission.Terminal, PermissionGrant.Ask), (AgentPermission.Git, PermissionGrant.Ask),
                (AgentPermission.PackageInstall, PermissionGrant.Ask), (AgentPermission.Network, PermissionGrant.Ask),
                (AgentPermission.LocalServices, PermissionGrant.Allow)), true),
        new(AdvancedId, "Advanced Development", "For experienced users: edits and most commands without asking. Never bypasses the agent's own safety checks.",
            With(PermissionGrant.Deny,
                (AgentPermission.FileRead, PermissionGrant.Allow), (AgentPermission.FileCreate, PermissionGrant.Allow),
                (AgentPermission.FileWrite, PermissionGrant.Allow), (AgentPermission.FileDelete, PermissionGrant.Ask),
                (AgentPermission.DirectoryCreate, PermissionGrant.Allow), (AgentPermission.ProcessExecution, PermissionGrant.Allow),
                (AgentPermission.Terminal, PermissionGrant.Allow), (AgentPermission.Git, PermissionGrant.Allow),
                (AgentPermission.PackageInstall, PermissionGrant.Ask), (AgentPermission.Network, PermissionGrant.Ask),
                (AgentPermission.LocalServices, PermissionGrant.Allow), (AgentPermission.OutsideWorkspace, PermissionGrant.Ask)), true),
    ];

    public static PermissionProfile Get(string id) =>
        All().FirstOrDefault(p => p.Id == id) ?? All().First(p => p.Id == DefaultId);
}
