using DarkCipher.AgentControl.Domain.Common;
using DarkCipher.AgentControl.Domain.Security;

namespace DarkCipher.AgentControl.Domain.Agents;

/// <summary>Port: agent permission profiles (built-in + custom) and effective-profile calculation for a launch.</summary>
public interface IPermissionService
{
    IReadOnlyList<PermissionProfile> Profiles { get; }
    PermissionProfile Resolve(string? id);
    (PermissionProfile Profile, string? Note) Effective(PermissionProfile requested, PathDecision projectDecision);
    OperationResult SaveCustom(PermissionProfile profile);
}
