using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SharedMailbox.Core.Configuration;
using SharedMailbox.Core.Services;

namespace SharedMailbox.Tests.Core.Services;

/// <summary>
/// Tests for <see cref="DefaultUserAuthorizationService"/>. The service is pure logic on
/// top of <see cref="IUserGroupMembershipProvider"/> and <see cref="IConnectionService"/>,
/// both of which we mock with NSubstitute — no live Graph or runspace required.
///
/// Covers the four <see cref="UserAuthorizationStatus"/> branches plus the edge cases
/// that affect them (missing UPN, empty Roles config, transient query failure).
/// </summary>
public class DefaultUserAuthorizationServiceTests
{
    private static readonly Guid PMRoleGroupId   = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OpsRoleGroupId  = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid PMGroupAId      = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid PMGroupBId      = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OpsGroupCId     = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid UnrelatedGroupId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private readonly IUserGroupMembershipProvider _memberships = Substitute.For<IUserGroupMembershipProvider>();
    private readonly IConnectionService _connection = Substitute.For<IConnectionService>();

    // -----------------------------------------------------------------------
    // NotConfigured branch — no Roles in config
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_RolesEmpty_ReturnsNotConfiguredWithoutHittingGraph()
    {
        var service = MakeService(new AppConfig
        {
            // Roles is the implicit default — empty array.
        });

        var result = await service.ResolveAsync();

        result.Status.Should().Be(UserAuthorizationStatus.NotConfigured);
        result.AssignedRoles.Should().BeEmpty();
        result.AllowedGroupIds.Should().BeEmpty();

        // No Graph round-trip should happen when Roles is empty — that's the point of
        // the short-circuit.
        await _memberships.DidNotReceive().GetMembershipsAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // NotAuthorized branches
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_NoSignedInUser_ReturnsNotAuthorized()
    {
        // Roles are configured but ConnectionStatus has no SignedInUser. The service
        // returns NotAuthorized rather than NotConfigured because the *intent* of
        // having Roles configured is filtering; we just can't resolve the filter yet.
        _connection.Status.Returns(new ConnectionStatus(
            ExchangeOnlineConnected: false,
            GraphConnected: false,
            SignedInUser: null,
            TenantId: null));

        var service = MakeService(ConfigWithRoles(
            new RoleConfig
            {
                Name = "Project Managers",
                EntraGroupId = PMRoleGroupId,
                AllowedGroupIds = new[] { PMGroupAId },
            }));

        var result = await service.ResolveAsync();

        result.Status.Should().Be(UserAuthorizationStatus.NotAuthorized);
    }

    [Fact]
    public async Task ResolveAsync_SignedInUserInNoMappedGroup_ReturnsNotAuthorized()
    {
        _connection.Status.Returns(SignedIn("user@tenant.com"));
        _memberships.GetMembershipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { UnrelatedGroupId });

        var service = MakeService(ConfigWithRoles(
            new RoleConfig
            {
                Name = "Project Managers",
                EntraGroupId = PMRoleGroupId,
                AllowedGroupIds = new[] { PMGroupAId },
            }));

        var result = await service.ResolveAsync();

        result.Status.Should().Be(UserAuthorizationStatus.NotAuthorized);
        result.AssignedRoles.Should().BeEmpty();
        result.AllowedGroupIds.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Authorized branch — the happy path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_UserInSingleRole_ReturnsThatRoleAndItsAllowedGroups()
    {
        _connection.Status.Returns(SignedIn("pm@tenant.com"));
        _memberships.GetMembershipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { PMRoleGroupId });

        var service = MakeService(ConfigWithRoles(
            new RoleConfig
            {
                Name = "Project Managers",
                EntraGroupId = PMRoleGroupId,
                AllowedGroupIds = new[] { PMGroupAId, PMGroupBId },
            },
            new RoleConfig
            {
                Name = "Ops Leads",
                EntraGroupId = OpsRoleGroupId,
                AllowedGroupIds = new[] { OpsGroupCId },
            }));

        var result = await service.ResolveAsync();

        result.Status.Should().Be(UserAuthorizationStatus.Authorized);
        result.AssignedRoles.Should().ContainSingle().Which.Name.Should().Be("Project Managers");
        result.AllowedGroupIds.Should().BeEquivalentTo(new[] { PMGroupAId, PMGroupBId });
    }

    [Fact]
    public async Task ResolveAsync_UserInMultipleRoles_UnionsAllAllowedGroups()
    {
        _connection.Status.Returns(SignedIn("multi@tenant.com"));
        _memberships.GetMembershipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { PMRoleGroupId, OpsRoleGroupId });

        var service = MakeService(ConfigWithRoles(
            new RoleConfig
            {
                Name = "Project Managers",
                EntraGroupId = PMRoleGroupId,
                AllowedGroupIds = new[] { PMGroupAId, PMGroupBId },
            },
            new RoleConfig
            {
                Name = "Ops Leads",
                EntraGroupId = OpsRoleGroupId,
                AllowedGroupIds = new[] { OpsGroupCId },
            }));

        var result = await service.ResolveAsync();

        result.Status.Should().Be(UserAuthorizationStatus.Authorized);
        result.AssignedRoles.Should().HaveCount(2);
        result.AllowedGroupIds.Should().BeEquivalentTo(new[] { PMGroupAId, PMGroupBId, OpsGroupCId });
    }

    [Fact]
    public async Task ResolveAsync_OverlappingAllowedGroups_AreDedupedInUnion()
    {
        // Two roles both grant access to PMGroupA — the union should still report it once.
        _connection.Status.Returns(SignedIn("multi@tenant.com"));
        _memberships.GetMembershipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { PMRoleGroupId, OpsRoleGroupId });

        var service = MakeService(ConfigWithRoles(
            new RoleConfig
            {
                EntraGroupId = PMRoleGroupId,
                AllowedGroupIds = new[] { PMGroupAId, PMGroupBId },
            },
            new RoleConfig
            {
                EntraGroupId = OpsRoleGroupId,
                AllowedGroupIds = new[] { PMGroupAId, OpsGroupCId },
            }));

        var result = await service.ResolveAsync();

        result.AllowedGroupIds.Should().HaveCount(3);
        result.AllowedGroupIds.Should().BeEquivalentTo(new[] { PMGroupAId, PMGroupBId, OpsGroupCId });
    }

    // -----------------------------------------------------------------------
    // LookupFailed branch — fail closed
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_MembershipQueryThrows_ReturnsLookupFailedWithMessage()
    {
        _connection.Status.Returns(SignedIn("pm@tenant.com"));
        _memberships.GetMembershipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Graph timeout"));

        var service = MakeService(ConfigWithRoles(
            new RoleConfig { EntraGroupId = PMRoleGroupId, AllowedGroupIds = new[] { PMGroupAId } }));

        var result = await service.ResolveAsync();

        result.Status.Should().Be(UserAuthorizationStatus.LookupFailed);
        result.ErrorMessage.Should().Contain("Graph timeout");
        result.AllowedGroupIds.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private DefaultUserAuthorizationService MakeService(AppConfig config) =>
        new(config, _memberships, _connection, NullLogger<DefaultUserAuthorizationService>.Instance);

    private static AppConfig ConfigWithRoles(params RoleConfig[] roles) => new()
    {
        Roles = roles,
    };

    private static ConnectionStatus SignedIn(string upn) => new(
        ExchangeOnlineConnected: true,
        GraphConnected: true,
        SignedInUser: upn,
        TenantId: "tenant.onmicrosoft.com");
}
