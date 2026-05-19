namespace SharedMailbox.Core.Configuration;

/// <summary>
/// One entry in the <see cref="AppConfig.Roles"/> list. Maps an Entra security group of
/// *users* (the "ROLE-..." group) to the set of SharedMail- groups its members are
/// permitted to administer.
///
/// At sign-in, the app fetches the running user's group memberships via Microsoft Graph
/// and matches them against <see cref="EntraGroupId"/>. Each matched role contributes its
/// <see cref="AllowedGroupIds"/> to the user's effective access set, and the sidebar's
/// SharedMail- group list is filtered to that union.
///
/// This is Layer 2 of the dual-layer security model in Architecture.md §6.2 — UX-layer
/// filtering for clarity and least-effort. Layer 1 (Exchange Online RBAC management
/// scopes) is configured separately per Setup.md §3.4 and is what enforces the boundary
/// at the platform level.
/// </summary>
public sealed class RoleConfig
{
    /// <summary>Human-readable display name (e.g., "Project Managers"). Shown in logs.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Object ID of the Entra security group whose members hold this role.</summary>
    public Guid EntraGroupId { get; init; }

    /// <summary>
    /// Object IDs of the SharedMail- groups this role can manage. These should appear in
    /// <see cref="AppConfig.KnownGroups"/> — if they don't, they'll be invisible in the UI
    /// even though the role permits them. (Mirrors how the sidebar works today: only
    /// KnownGroups are shown.)
    /// </summary>
    public IReadOnlyList<Guid> AllowedGroupIds { get; init; } = Array.Empty<Guid>();
}
