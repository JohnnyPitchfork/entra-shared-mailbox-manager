using FluentAssertions;
using SharedMailbox.Core.Domain;

namespace SharedMailbox.Tests.Core.Domain;

public class DelegateReportTests
{
    [Fact]
    public void GrantedRights_NoRights_None()
    {
        var report = Row(fa: false, sa: false, sob: false);
        report.GrantedRights.Should().Be(AccessRight.None);
    }

    [Fact]
    public void GrantedRights_AllRights_AllFlagsCombined()
    {
        var report = Row(fa: true, sa: true, sob: true);
        report.GrantedRights.Should().Be(
            AccessRight.FullAccess | AccessRight.SendAs | AccessRight.SendOnBehalf);
        report.GrantedRights.Should().Be(AccessRight.All);
    }

    [Theory]
    [InlineData(true,  false, false, AccessRight.FullAccess)]
    [InlineData(false, true,  false, AccessRight.SendAs)]
    [InlineData(false, false, true,  AccessRight.SendOnBehalf)]
    [InlineData(true,  true,  false, AccessRight.FullAccess | AccessRight.SendAs)]
    [InlineData(true,  false, true,  AccessRight.FullAccess | AccessRight.SendOnBehalf)]
    [InlineData(false, true,  true,  AccessRight.SendAs | AccessRight.SendOnBehalf)]
    public void GrantedRights_PerCombination(bool fa, bool sa, bool sob, AccessRight expected)
    {
        Row(fa, sa, sob).GrantedRights.Should().Be(expected);
    }

    private static DelegateReport Row(bool fa, bool sa, bool sob) =>
        new()
        {
            Mailbox = "shared@tenant.com",
            Trustee = "trustee@tenant.com",
            FullAccess = fa,
            SendAs = sa,
            SendOnBehalf = sob,
        };
}
