using SharedMailbox.Core.Configuration;

namespace SharedMailbox.Core.Services;

/// <summary>
/// Resolves the current signed-in user's role-based authorization from
/// <see cref="AppConfig.Roles"/> and the user's actual group memberships. Consumed by
/// <c>GroupPickerViewModel</c> to filter the sidebar after sign-in.
///
/// Backward-compatible: when <see cref="AppConfig.Roles"/> is empty, the result is
/// <see cref="UserAuthorizationStatus.NotConfigured"/> and the caller treats the user
/// as having access to every <see cref="AppConfig.KnownGroups"/> entry.
/// </summary>
public interface IUserAuthorizationService
{
    /// <summary>
    /// Resolves the authorization state for the currently signed-in user. Returns
    /// quickly with <see cref="UserAuthorizationStatus.NotConfigured"/> when no Roles
    /// are configured (no Graph round-trip needed).
    /// </summary>
    Task<UserAuthorization> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The result of resolving a user's authorization. Inspect <see cref="Status"/> to
/// branch on the four outcomes; <see cref="AssignedRoles"/> and <see cref="AllowedGroupIds"/>
/// are populated only when <see cref="Status"/> is <see cref="UserAuthorizationStatus.Authorized"/>.
/// </summary>
public sealed record UserAuthorization(
    UserAuthorizationStatus Status,
    IReadOnlyList<RoleConfig> AssignedRoles,
    IReadOnlySet<Guid> AllowedGroupIds,
    string? ErrorMessage = null)
{
    /// <summary>No Roles in config — every KnownGroup is accessible.</summary>
    public static UserAuthorization NotConfigured() => new(
        UserAuthorizationStatus.NotConfigured,
        Array.Empty<RoleConfig>(),
        new HashSet<Guid>());

    /// <summary>User holds at least one role; the sidebar filters to AllowedGroupIds.</summary>
    public static UserAuthorization Authorized(
        IReadOnlyList<RoleConfig> roles,
        IReadOnlySet<Guid> allowedGroupIds) =>
        new(UserAuthorizationStatus.Authorized, roles, allowedGroupIds);

    /// <summary>Roles are configured but the user is in none. Sidebar is empty + message.</summary>
    public static UserAuthorization NotAuthorized() => new(
        UserAuthorizationStatus.NotAuthorized,
        Array.Empty<RoleConfig>(),
        new HashSet<Guid>());

    /// <summary>Couldn't fetch the user's memberships (transient Graph error). Fail-closed.</summary>
    public static UserAuthorization LookupFailed(string errorMessage) => new(
        UserAuthorizationStatus.LookupFailed,
        Array.Empty<RoleConfig>(),
        new HashSet<Guid>(),
        errorMessage);
}

public enum UserAuthorizationStatus
{
    /// <summary>No Roles configured. All KnownGroups are accessible (legacy behavior).</summary>
    NotConfigured,

    /// <summary>User is in at least one configured role. Filtering applies.</summary>
    Authorized,

    /// <summary>Roles configured, user is in none. No groups accessible.</summary>
    NotAuthorized,

    /// <summary>Graph membership query failed. Fail-closed — no groups shown.</summary>
    LookupFailed,
}
