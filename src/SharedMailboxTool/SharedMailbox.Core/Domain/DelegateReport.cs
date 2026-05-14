namespace SharedMailbox.Core.Domain;

/// <summary>
/// One row per (mailbox, trustee) describing what permissions the trustee holds on the
/// mailbox and whether their Entra account is enabled. Produced by the audit workflow
/// and consumed by the cleanup workflow.
///
/// Mirrors the PSCustomObject emitted by Get-MailboxDelegatesAndStatus in the script.
/// The audit log CSV is just a serialization of these.
/// </summary>
public sealed record DelegateReport
{
    /// <summary>The UPN of the shared mailbox.</summary>
    public required string Mailbox { get; init; }

    /// <summary>The resolved UPN of the user who holds delegated rights on the mailbox.</summary>
    public required string Trustee { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Null when LookupStatus != Ok.</summary>
    public bool? AccountEnabled { get; init; }

    /// <summary>Null when LookupStatus != Ok. Equivalent to !AccountEnabled otherwise.</summary>
    public bool? SignInBlocked { get; init; }

    public bool FullAccess { get; init; }
    public bool SendAs { get; init; }
    public bool SendOnBehalf { get; init; }

    /// <summary>
    /// True when the audit was run with includeSendAs=true. When false, the SendAs column
    /// in the CSV is recorded as "Skipped" rather than a boolean (matching the script).
    /// </summary>
    public bool SendAsScanned { get; init; }

    public UserLookupStatus LookupStatus { get; init; }

    /// <summary>Convenience: the flags-style combination of permissions held.</summary>
    public AccessRight GrantedRights
    {
        get
        {
            var rights = AccessRight.None;
            if (FullAccess)   rights |= AccessRight.FullAccess;
            if (SendAs)       rights |= AccessRight.SendAs;
            if (SendOnBehalf) rights |= AccessRight.SendOnBehalf;
            return rights;
        }
    }
}
