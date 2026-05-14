namespace SharedMailbox.Core.Domain;

/// <summary>
/// One row per (user × shared mailbox) attempt during the add-users-to-group flow.
/// Mirrors the CSV exported by Invoke-AddUsersToAllMailboxesInGroup
/// (columns: User_UPN, Access_Status, SendAs_Status, Shared_Mailbox_Address).
/// </summary>
public sealed record BulkAddResult(
    string UserUpn,
    string SharedMailboxAddress,
    PermissionOutcome FullAccessOutcome,
    PermissionOutcome SendAsOutcome,
    string? AccessStatusMessage,
    string? SendAsStatusMessage)
{
    public bool AnyFailure =>
        FullAccessOutcome == PermissionOutcome.Failed ||
        SendAsOutcome    == PermissionOutcome.Failed;
}

public enum PermissionOutcome
{
    /// <summary>The permission was not requested for this run (e.g., SendAs skipped).</summary>
    NotAttempted,

    /// <summary>The user already held this permission; nothing was changed.</summary>
    AlreadyPresent,

    /// <summary>The permission was successfully granted.</summary>
    Granted,

    /// <summary>An exception was thrown during the grant attempt; see the status message.</summary>
    Failed,
}
