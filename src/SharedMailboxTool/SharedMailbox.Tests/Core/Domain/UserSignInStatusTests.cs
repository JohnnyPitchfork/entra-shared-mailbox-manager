using FluentAssertions;
using SharedMailbox.Core.Domain;

namespace SharedMailbox.Tests.Core.Domain;

public class UserSignInStatusTests
{
    [Fact]
    public void SignInBlocked_OkAndDisabled_True()
    {
        var status = new UserSignInStatus(
            UserLookupStatus.Ok,
            DisplayName: "Jane Doe",
            UserPrincipalName: "jane@tenant.com",
            AccountEnabled: false);

        status.SignInBlocked.Should().BeTrue();
    }

    [Fact]
    public void SignInBlocked_OkAndEnabled_False()
    {
        var status = new UserSignInStatus(
            UserLookupStatus.Ok,
            "Jane Doe", "jane@tenant.com", AccountEnabled: true);

        status.SignInBlocked.Should().BeFalse();
    }

    [Fact]
    public void SignInBlocked_LookupFailed_Null()
    {
        // When Graph lookup fails, we cannot infer block state. Mirrors the script:
        // a missing user is recorded as LOOKUP_FAILED with null AccountEnabled, and
        // SignInBlocked must stay null so cleanup doesn't act on the row.
        var status = new UserSignInStatus(
            UserLookupStatus.LookupFailed,
            DisplayName: null,
            UserPrincipalName: "ghost@tenant.com",
            AccountEnabled: null);

        status.SignInBlocked.Should().BeNull();
    }

    [Fact]
    public void SignInBlocked_OkButAccountEnabledIsNull_Null()
    {
        // Defensive case — Get-MgUser returned a user but didn't populate accountEnabled.
        var status = new UserSignInStatus(
            UserLookupStatus.Ok,
            "Jane Doe", "jane@tenant.com", AccountEnabled: null);

        status.SignInBlocked.Should().BeNull();
    }

    [Fact]
    public void LookupFailed_FactoryProducesExpectedShape()
    {
        var status = UserSignInStatus.LookupFailed("missing@tenant.com");

        status.LookupStatus.Should().Be(UserLookupStatus.LookupFailed);
        status.UserPrincipalName.Should().Be("missing@tenant.com");
        status.DisplayName.Should().BeNull();
        status.AccountEnabled.Should().BeNull();
        status.SignInBlocked.Should().BeNull();
    }
}
