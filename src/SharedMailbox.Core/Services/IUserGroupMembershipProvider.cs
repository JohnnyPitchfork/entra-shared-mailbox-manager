namespace SharedMailbox.Core.Services;

/// <summary>
/// Fetches the object IDs of the Entra security groups the given user is a direct member
/// of. Implementations talk to Microsoft Graph (the production impl in the PowerShell
/// project wraps <c>Get-MgUserMemberOf</c>); a fake implementation can be substituted in
/// tests.
///
/// Direct memberships only — for v1.0 we don't traverse nested groups. Tenants that nest
/// their ROLE-* groups inside parent groups should either flatten the structure or wait
/// for transitive support in a later version.
/// </summary>
public interface IUserGroupMembershipProvider
{
    /// <summary>
    /// Returns the group object IDs the given user is a direct member of. The user is
    /// identified by UPN (recommended) or by object ID — both are accepted by Graph's
    /// <c>Get-MgUserMemberOf</c>.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetMembershipsAsync(
        string userId,
        CancellationToken cancellationToken = default);
}
