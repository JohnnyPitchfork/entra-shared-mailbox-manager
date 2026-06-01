using SharedMailbox.Core.Domain;

namespace SharedMailbox.Core.Services;

/// <summary>
/// Writes the three CSV log formats the original script produced:
///   * mailbox-audit-{yyyyMMdd-HHmmss}.csv      (from Path 2 / AuditAsync)
///   * mailbox-cleanup-{yyyyMMdd-HHmmss}.csv    (from Path 3 / RemoveDelegatesAsync)
///   * SharedMail-BulkAction-{yyyyMMdd-HHmmss}.csv  (from Path 1 / AddUsersToMailboxesAsync)
///
/// The column order and value formatting are kept exactly compatible with the
/// PowerShell exports so existing log readers / Excel templates still work.
/// </summary>
public interface IAuditLogWriter
{
    Task<string> WriteAuditAsync(IReadOnlyList<DelegateReport> rows, CancellationToken cancellationToken = default);

    Task<string> WriteCleanupAsync(IReadOnlyList<CleanupAction> rows, CancellationToken cancellationToken = default);

    Task<string> WriteBulkAddAsync(IReadOnlyList<BulkAddResult> rows, CancellationToken cancellationToken = default);
}
