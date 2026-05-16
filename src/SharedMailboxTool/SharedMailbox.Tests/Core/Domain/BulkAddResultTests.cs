using FluentAssertions;
using SharedMailbox.Core.Domain;

namespace SharedMailbox.Tests.Core.Domain;

public class BulkAddResultTests
{
    [Theory]
    // Both outcomes succeed → no failure.
    [InlineData(PermissionOutcome.Granted,        PermissionOutcome.Granted,        false)]
    [InlineData(PermissionOutcome.AlreadyPresent, PermissionOutcome.Granted,        false)]
    [InlineData(PermissionOutcome.AlreadyPresent, PermissionOutcome.AlreadyPresent, false)]
    // NotAttempted (e.g., SendAs intentionally skipped) is not a failure.
    [InlineData(PermissionOutcome.Granted,        PermissionOutcome.NotAttempted,   false)]
    [InlineData(PermissionOutcome.NotAttempted,   PermissionOutcome.NotAttempted,   false)]
    // Any Failed outcome flips AnyFailure to true.
    [InlineData(PermissionOutcome.Failed,         PermissionOutcome.Granted,        true)]
    [InlineData(PermissionOutcome.Granted,        PermissionOutcome.Failed,         true)]
    [InlineData(PermissionOutcome.Failed,         PermissionOutcome.Failed,         true)]
    [InlineData(PermissionOutcome.Failed,         PermissionOutcome.NotAttempted,   true)]
    public void AnyFailure_TrueIffAnOutcomeIsFailed(
        PermissionOutcome fullAccess,
        PermissionOutcome sendAs,
        bool expected)
    {
        var result = new BulkAddResult(
            UserUpn: "user@tenant.com",
            SharedMailboxAddress: "shared@tenant.com",
            FullAccessOutcome: fullAccess,
            SendAsOutcome: sendAs,
            AccessStatusMessage: null,
            SendAsStatusMessage: null);

        result.AnyFailure.Should().Be(expected);
    }
}
