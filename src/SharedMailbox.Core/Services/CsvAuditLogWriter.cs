using System.Text;
using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Configuration;
using SharedMailbox.Core.Domain;

namespace SharedMailbox.Core.Services;

/// <summary>
/// Default <see cref="IAuditLogWriter"/>. Writes UTF-8 CSV files into the configured
/// log directory using filenames and column orders that match the original PowerShell
/// exports byte-for-byte, so the same downstream tooling (Excel templates, log readers)
/// keeps working.
///
/// Column layouts kept identical to:
///   * Get-MailboxDelegatesAndStatus → Export-Csv mailbox-audit-{ts}.csv
///   * Remove-BlockedDelegates       → Export-Csv mailbox-cleanup-{ts}.csv
///   * Invoke-AddUsersToAllMailboxesInGroup → Export-Csv SharedMail-BulkAction-{ts}.csv
/// </summary>
public sealed class CsvAuditLogWriter : IAuditLogWriter
{
    private readonly LoggingConfig _logging;
    private readonly ILogger<CsvAuditLogWriter> _logger;

    public CsvAuditLogWriter(LoggingConfig logging, ILogger<CsvAuditLogWriter> logger)
    {
        _logging = logging ?? throw new ArgumentNullException(nameof(logging));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<string> WriteAuditAsync(IReadOnlyList<DelegateReport> rows, CancellationToken cancellationToken = default)
    {
        // Sort matches the PS script's Sort-Object: SignInBlocked desc, then Mailbox, then Trustee.
        var sorted = rows
            .OrderByDescending(r => r.SignInBlocked == true)
            .ThenBy(r => r.Mailbox, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Trustee, StringComparer.OrdinalIgnoreCase);

        var headers = new[]
        {
            "Mailbox", "Trustee", "DisplayName", "AccountEnabled", "SignInBlocked",
            "FullAccess", "SendAs", "SendOnBehalf", "LookupStatus",
        };

        return WriteAsync(
            prefix: "mailbox-audit",
            headers: headers,
            data: sorted.Select(r => new[]
            {
                r.Mailbox,
                r.Trustee,
                r.DisplayName ?? string.Empty,
                FormatNullableBool(r.AccountEnabled),
                FormatNullableBool(r.SignInBlocked),
                FormatBool(r.FullAccess),
                // When SendAs was not scanned the script wrote the literal "Skipped".
                r.SendAsScanned ? FormatBool(r.SendAs) : "Skipped",
                FormatBool(r.SendOnBehalf),
                r.LookupStatus switch
                {
                    UserLookupStatus.Ok => "OK",
                    UserLookupStatus.LookupFailed => "LOOKUP_FAILED",
                    _ => r.LookupStatus.ToString(),
                },
            }),
            cancellationToken: cancellationToken);
    }

    public Task<string> WriteCleanupAsync(IReadOnlyList<CleanupAction> rows, CancellationToken cancellationToken = default)
    {
        var headers = new[] { "Mailbox", "Trustee", "Action", "Result", "Notes" };

        return WriteAsync(
            prefix: "mailbox-cleanup",
            headers: headers,
            data: rows.Select(r => new[]
            {
                r.Mailbox,
                r.Trustee,
                ActionLabel(r.Right),
                r.Result.ToString(),
                r.Notes ?? string.Empty,
            }),
            cancellationToken: cancellationToken);
    }

    public Task<string> WriteBulkAddAsync(IReadOnlyList<BulkAddResult> rows, CancellationToken cancellationToken = default)
    {
        // Column names preserved exactly so existing log consumers don't break.
        var headers = new[] { "User_UPN", "Access_Status", "SendAs_Status", "Shared_Mailbox_Address" };

        return WriteAsync(
            prefix: "SharedMail-BulkAction",
            headers: headers,
            data: rows.Select(r => new[]
            {
                r.UserUpn,
                r.AccessStatusMessage ?? OutcomeLabel(r.FullAccessOutcome, "FullAccess"),
                r.SendAsStatusMessage ?? OutcomeLabel(r.SendAsOutcome, "SendAs"),
                r.SharedMailboxAddress,
            }),
            cancellationToken: cancellationToken);
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private async Task<string> WriteAsync(
        string prefix,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<string>> data,
        CancellationToken cancellationToken)
    {
        var dir = ResolveLogDirectory();
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        _logger.LogInformation("Writing {Prefix} CSV to {Path}", prefix, path);

        // UTF-8 with BOM matches what PowerShell's Export-Csv -Encoding UTF8 produces,
        // which Excel needs in order to detect Unicode correctly.
        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, utf8WithBom);

        await writer.WriteLineAsync(string.Join(',', headers.Select(EscapeCsv))).ConfigureAwait(false);
        var rowCount = 0;
        foreach (var row in data)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(',', row.Select(EscapeCsv))).ConfigureAwait(false);
            rowCount++;
        }

        await writer.FlushAsync().ConfigureAwait(false);
        _logger.LogDebug("Wrote {Count} rows to {Path}", rowCount, path);
        return path;
    }

    private string ResolveLogDirectory()
    {
        var configured = _logging.LogDirectory;
        if (string.IsNullOrWhiteSpace(configured)) configured = "Logs";

        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var needsQuoting = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!needsQuoting) return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string FormatBool(bool value) =>
        value ? "True" : "False"; // Matches PowerShell Export-Csv output exactly.

    private static string FormatNullableBool(bool? value) =>
        value is null ? string.Empty : FormatBool(value.Value);

    private static string ActionLabel(AccessRight right) => right switch
    {
        AccessRight.FullAccess   => "RemoveFullAccess",
        AccessRight.SendAs       => "RemoveSendAs",
        AccessRight.SendOnBehalf => "RemoveSendOnBehalf",
        _                        => $"Remove{right}",
    };

    private static string OutcomeLabel(PermissionOutcome outcome, string rightName) => outcome switch
    {
        PermissionOutcome.AlreadyPresent => $"{rightName} already present",
        PermissionOutcome.Granted        => $"{rightName} granted",
        PermissionOutcome.Failed         => $"Failed to grant {rightName}",
        PermissionOutcome.NotAttempted   => "Not attempted",
        _                                => outcome.ToString(),
    };
}
