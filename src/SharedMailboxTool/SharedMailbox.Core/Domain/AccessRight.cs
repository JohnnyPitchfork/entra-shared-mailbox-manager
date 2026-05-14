namespace SharedMailbox.Core.Domain;

/// <summary>
/// The three kinds of delegated permission this tool grants or revokes on a shared mailbox.
/// Modeled as flags so a single value can describe a trustee's combined permissions on one mailbox.
/// </summary>
[Flags]
public enum AccessRight
{
    None         = 0,
    FullAccess   = 1 << 0,
    SendAs       = 1 << 1,
    SendOnBehalf = 1 << 2,

    /// <summary>FullAccess + SendAs. The default grant from Invoke-AddUsersToAllMailboxesInGroup.</summary>
    StandardGrant = FullAccess | SendAs,

    /// <summary>All three. The set the cleanup workflow may revoke.</summary>
    All = FullAccess | SendAs | SendOnBehalf,
}
