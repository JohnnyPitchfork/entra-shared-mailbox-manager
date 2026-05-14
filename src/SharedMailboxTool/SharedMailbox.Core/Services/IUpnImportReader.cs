namespace SharedMailbox.Core.Services;

/// <summary>
/// Reads a CSV with a single 'UPN' column and returns the deduped, trimmed, non-empty
/// list of UPNs. Mirrors the inline CSV-import block in the PowerShell script's
/// Invoke-AddUsersToAllMailboxesInGroup function.
///
/// Throws <see cref="UpnImportException"/> for either of the script's two rejection cases:
///   * The file has no 'UPN' column.
///   * The 'UPN' column has no non-empty values.
/// </summary>
public interface IUpnImportReader
{
    Task<IReadOnlyList<string>> ReadAsync(string csvPath, CancellationToken cancellationToken = default);
}

public sealed class UpnImportException : Exception
{
    public UpnImportException(string message) : base(message) { }
    public UpnImportException(string message, Exception inner) : base(message, inner) { }
}
