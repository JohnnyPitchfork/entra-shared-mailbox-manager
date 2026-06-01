using FluentAssertions;
using SharedMailbox.Core.Domain;

namespace SharedMailbox.Tests.Core.Domain;

public class MailboxTests
{
    [Theory]
    [InlineData(RecipientTypeDetails.SharedMailbox,     true)]
    [InlineData(RecipientTypeDetails.UserMailbox,       false)]
    [InlineData(RecipientTypeDetails.RoomMailbox,       false)]
    [InlineData(RecipientTypeDetails.EquipmentMailbox,  false)]
    [InlineData(RecipientTypeDetails.DiscoveryMailbox,  false)]
    [InlineData(RecipientTypeDetails.MailUser,          false)]
    [InlineData(RecipientTypeDetails.GuestMailUser,     false)]
    [InlineData(RecipientTypeDetails.GroupMailbox,      false)]
    [InlineData(RecipientTypeDetails.Unknown,           false)]
    public void IsSharedMailbox_TrueOnlyForSharedMailboxType(
        RecipientTypeDetails type, bool expected)
    {
        var mailbox = new Mailbox(Guid.NewGuid(), "shared@tenant.com", type);
        mailbox.IsSharedMailbox.Should().Be(expected);
    }

    [Fact]
    public void ToString_ReturnsUserPrincipalName()
    {
        var mailbox = new Mailbox(Guid.NewGuid(), "shared@tenant.com", RecipientTypeDetails.SharedMailbox);
        mailbox.ToString().Should().Be("shared@tenant.com");
    }
}
