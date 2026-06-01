using SharedMailbox.Core.Domain;

namespace SharedMailbox.Core.Services;

/// <summary>
/// High-level orchestrator the UI consumes. Every operation is async, cancellable, and
/// emits progress so view models can drive a progress bar without knowing whether the
/// underlying work runs in a PowerShell runspace or via Graph SDK calls.
///
/// One instance per session. Implementations must be thread-safe for sequential calls
/// from the UI but are not expected to support concurrent calls from multiple threads.
/// </summary>
public interface ISharedMailboxService
{
    /// <summary>
    /// List members of the given SharedMail- group, filtered to entries that have a UPN
    /// (matches the PS: Where-Object userPrincipalName -like '*@*').
    /// Returned items have RecipientType=Unknown — the caller can pass these UPNs through
    /// <see cref="FilterSharedMailboxesAsync"/> when a verified type is required.
    /// </summary>
    Task<IReadOnlyList<Mailbox>> GetGroupMembersAsync(
        Guid groupId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolve each UPN against EXO Get-Mailbox and return only the ones whose
    /// RecipientTypeDetails is SharedMailbox. Mirrors Get-SharedMailboxesOnly in the script.
    /// Items the caller can't resolve are silently skipped (warnings flow through ILogger).
    /// </summary>
    Task<IReadOnlyList<Mailbox>> FilterSharedMailboxesAsync(
        IReadOnlyList<string> upns,
        IProgress<MailboxOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Audit one or more mailboxes for delegated permissions and Entra sign-in status.
    /// Returns one row per (mailbox, trustee). Equivalent to running Path 2 in the script
    /// (Get-MailboxDelegatesAndStatus for each mailbox + accumulate).
    /// </summary>
    /// <param name="mailboxUpns">Shared mailbox UPNs to audit. Caller is responsible for
    /// having filtered these to SharedMailbox type if desired.</param>
    /// <param name="includeSendAs">When false, SendAs is not scanned and DelegateReport.SendAsScanned=false.</param>
    Task<IReadOnlyList<DelegateReport>> AuditAsync(
        IReadOnlyList<string> mailboxUpns,
        bool includeSendAs,
        IProgress<MailboxOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the granted rights for each supplied target row. The caller is responsible
    /// for selecting which rows to act on (typically: rows where SignInBlocked == true
    /// and the user has confirmed in the UI).
    ///
    /// Equivalent to Path 3 in the script (Remove-BlockedDelegates) but driven by the
    /// caller's selected rows rather than an interactive Y/N/A/Q prompt loop.
    /// </summary>
    Task<IReadOnlyList<CleanupAction>> RemoveDelegatesAsync(
        IReadOnlyList<DelegateReport> targets,
        bool includeSendAs,
        IProgress<MailboxOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grant FullAccess (and optionally SendAs) to each listed user across each listed
    /// shared mailbox. Equivalent to Path 1 in the script (Invoke-AddUsersToAllMailboxesInGroup),
    /// but expects the caller to have already resolved the mailbox list.
    /// </summary>
    /// <param name="grantSendAs">Matches the script's hard-coded behavior when true (FullAccess + SendAs).</param>
    Task<IReadOnlyList<BulkAddResult>> AddUsersToMailboxesAsync(
        IReadOnlyList<string> userUpns,
        IReadOnlyList<string> mailboxUpns,
        bool grantSendAs = true,
        IProgress<MailboxOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Progress envelope emitted by long-running operations. View models bind directly to this.
/// Fraction is computed from Completed/Total so the UI doesn't need to.
/// </summary>
public sealed record MailboxOperationProgress(
    int Completed,
    int Total,
    string Status,
    string? Detail = null)
{
    public double Fraction => Total <= 0 ? 0d : (double)Completed / Total;
}
