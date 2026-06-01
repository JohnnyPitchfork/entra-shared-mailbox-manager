using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Configuration;

namespace SharedMailbox.Core.Services;

/// <summary>
/// Default <see cref="IUserAuthorizationService"/>. Pure logic — delegates the actual
/// Graph query to <see cref="IUserGroupMembershipProvider"/>, then intersects the result
/// with <see cref="AppConfig.Roles"/> to compute the effective allowed-group set.
///
/// Lives in <c>Core</c> rather than the App or PowerShell project because the logic is
/// platform-agnostic: it operates on a config object, a UPN string, and a list of GUIDs.
/// The expensive part (the actual Graph round-trip) is what's adapter-specific.
/// </summary>
public sealed class DefaultUserAuthorizationService : IUserAuthorizationService
{
    private readonly AppConfig _appConfig;
    private readonly IUserGroupMembershipProvider _membershipProvider;
    private readonly IConnectionService _connectionService;
    private readonly ILogger<DefaultUserAuthorizationService> _logger;

    public DefaultUserAuthorizationService(
        AppConfig appConfig,
        IUserGroupMembershipProvider membershipProvider,
        IConnectionService connectionService,
        ILogger<DefaultUserAuthorizationService> logger)
    {
        _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
        _membershipProvider = membershipProvider ?? throw new ArgumentNullException(nameof(membershipProvider));
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserAuthorization> ResolveAsync(CancellationToken cancellationToken = default)
    {
        // Short-circuit: no Roles defined means no filtering at all. No Graph round-trip
        // needed; preserves the v1.0-pre-roles behavior for existing deployments.
        if (_appConfig.Roles is null || _appConfig.Roles.Count == 0)
        {
            return UserAuthorization.NotConfigured();
        }

        // We need a UPN (or object ID) to query Graph against. The IConnectionService
        // populates SignedInUser as part of Status once Connect-MgGraph completes.
        var signedInUser = _connectionService.Status.SignedInUser;
        if (string.IsNullOrEmpty(signedInUser))
        {
            _logger.LogDebug(
                "ResolveAsync called with no signed-in user; returning NotAuthorized as a safe default");
            return UserAuthorization.NotAuthorized();
        }

        IReadOnlyList<Guid> userMemberships;
        try
        {
            userMemberships = await _membershipProvider
                .GetMembershipsAsync(signedInUser, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to fetch group memberships for {User}", signedInUser);
            return UserAuthorization.LookupFailed(ex.Message);
        }

        var membershipSet = userMemberships.ToHashSet();
        var assignedRoles = _appConfig.Roles
            .Where(r => membershipSet.Contains(r.EntraGroupId))
            .ToList();

        if (assignedRoles.Count == 0)
        {
            _logger.LogInformation(
                "User {User} is in no configured role groups ({TotalRoles} role(s) defined)",
                signedInUser, _appConfig.Roles.Count);
            return UserAuthorization.NotAuthorized();
        }

        // Union of all allowed group IDs across the user's assigned roles.
        var allowedGroupIds = assignedRoles
            .SelectMany(r => r.AllowedGroupIds)
            .ToHashSet();

        _logger.LogInformation(
            "User {User} resolved to {RoleCount} role(s) granting access to {GroupCount} group(s)",
            signedInUser, assignedRoles.Count, allowedGroupIds.Count);

        return UserAuthorization.Authorized(assignedRoles, allowedGroupIds);
    }
}
