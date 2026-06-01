using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMailbox.Core.Configuration;
using SharedMailbox.Core.Domain;
using SharedMailbox.Core.Services;

namespace SharedMailbox.Tests.Core.Services;

/// <summary>
/// Tests for <see cref="CsvAuditLogWriter"/>. Each test writes to a per-test temp
/// directory and asserts the resulting file is byte-compatible with the format the
/// original PowerShell script produced (column names + order, True/False booleans,
/// Skipped sentinel, UTF-8 BOM, sort order, CSV escaping).
/// </summary>
public class CsvAuditLogWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CsvAuditLogWriter _writer;

    public CsvAuditLogWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"smt-csv-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _writer = new CsvAuditLogWriter(
            new LoggingConfig { LogDirectory = _tempDir },
            NullLogger<CsvAuditLogWriter>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* swallow */ }
    }

    // -----------------------------------------------------------------------
    // WriteAuditAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteAuditAsync_CreatesFileWithExpectedPrefixAndExtension()
    {
        var path = await _writer.WriteAuditAsync(Array.Empty<DelegateReport>());

        File.Exists(path).Should().BeTrue();
        Path.GetFileName(path).Should().StartWith("mailbox-audit-").And.EndWith(".csv");
        Path.GetDirectoryName(path).Should().Be(_tempDir);
    }

    [Fact]
    public async Task WriteAuditAsync_HeaderMatchesScriptOutput()
    {
        var path = await _writer.WriteAuditAsync(Array.Empty<DelegateReport>());
        var header = ReadHeaderLine(path);

        header.Should().Be(
            "Mailbox,Trustee,DisplayName,AccountEnabled,SignInBlocked,FullAccess,SendAs,SendOnBehalf,LookupStatus");
    }

    [Fact]
    public async Task WriteAuditAsync_WritesUtf8Bom()
    {
        var path = await _writer.WriteAuditAsync(Array.Empty<DelegateReport>());
        var bytes = await File.ReadAllBytesAsync(path);

        bytes.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF });
    }

    [Fact]
    public async Task WriteAuditAsync_FormatsBooleansAsCapitalizedTrueFalse()
    {
        // PowerShell's Export-Csv emits True/False capitalized; existing Excel templates
        // and downstream scripts rely on that exact casing.
        var rows = new[]
        {
            Report("a@t.com", "u@t.com",
                signInBlocked: true, fullAccess: true, sendAs: false, sendOnBehalf: true,
                lookupStatus: UserLookupStatus.Ok, accountEnabled: false, sendAsScanned: true),
        };

        var path = await _writer.WriteAuditAsync(rows);
        var dataLine = (await File.ReadAllLinesAsync(path)).Skip(1).First();

        dataLine.Should().Contain(",False,True,True,False,True,").And
            .Contain(",True,").And
            .NotContainAny("true,", ",false,"); // lowercase booleans would break consumers
    }

    [Fact]
    public async Task WriteAuditAsync_NullableBoolsBecomeEmptyWhenNull()
    {
        var rows = new[]
        {
            Report("a@t.com", "u@t.com",
                signInBlocked: null, fullAccess: false, sendAs: false, sendOnBehalf: false,
                lookupStatus: UserLookupStatus.LookupFailed, accountEnabled: null, sendAsScanned: true),
        };

        var path = await _writer.WriteAuditAsync(rows);
        var dataLine = (await File.ReadAllLinesAsync(path)).Skip(1).First();

        // Mailbox,Trustee,DisplayName, [AccountEnabled empty], [SignInBlocked empty], ...
        var fields = dataLine.Split(',');
        fields[3].Should().BeEmpty();  // AccountEnabled
        fields[4].Should().BeEmpty();  // SignInBlocked
    }

    [Fact]
    public async Task WriteAuditAsync_SendAsRendersSkippedWhenNotScanned()
    {
        // The script writes "Skipped" (literal string) in the SendAs column when the
        // -IncludeSendAs flag wasn't set. We preserve that exact value so existing
        // log readers don't have to special-case the missing scan.
        var rows = new[]
        {
            Report("a@t.com", "u@t.com",
                signInBlocked: false, fullAccess: true, sendAs: false, sendOnBehalf: false,
                lookupStatus: UserLookupStatus.Ok, accountEnabled: true,
                sendAsScanned: false /* <-- key */),
        };

        var path = await _writer.WriteAuditAsync(rows);
        var dataLine = (await File.ReadAllLinesAsync(path)).Skip(1).First();
        var fields = dataLine.Split(',');

        fields[6].Should().Be("Skipped");  // SendAs column index
    }

    [Fact]
    public async Task WriteAuditAsync_LookupStatusFormattedAsOkOrLookupFailed()
    {
        var rows = new[]
        {
            Report("a@t.com", "ok@t.com",
                signInBlocked: false, fullAccess: true, sendAs: false, sendOnBehalf: false,
                lookupStatus: UserLookupStatus.Ok, accountEnabled: true, sendAsScanned: true),
            Report("a@t.com", "ghost@t.com",
                signInBlocked: null, fullAccess: true, sendAs: false, sendOnBehalf: false,
                lookupStatus: UserLookupStatus.LookupFailed, accountEnabled: null, sendAsScanned: true),
        };

        var path = await _writer.WriteAuditAsync(rows);
        var lines = (await File.ReadAllLinesAsync(path)).Skip(1).ToArray();

        lines.Should().Contain(l => l.EndsWith(",OK"));
        lines.Should().Contain(l => l.EndsWith(",LOOKUP_FAILED"));
    }

    [Fact]
    public async Task WriteAuditAsync_SortsBySignInBlockedDescendingThenMailboxThenTrustee()
    {
        // Sort contract from CsvAuditLogWriter (mirrors the script):
        //   SignInBlocked DESC, Mailbox ASC, Trustee ASC.
        var rows = new[]
        {
            Report("zeta@t.com",  "alice@t.com", signInBlocked: false, lookupStatus: UserLookupStatus.Ok),
            Report("alpha@t.com", "bob@t.com",   signInBlocked: true,  lookupStatus: UserLookupStatus.Ok),
            Report("alpha@t.com", "alice@t.com", signInBlocked: true,  lookupStatus: UserLookupStatus.Ok),
            Report("alpha@t.com", "alice@t.com", signInBlocked: false, lookupStatus: UserLookupStatus.Ok)
                with { Mailbox = "alpha@t.com", Trustee = "carol@t.com" },
        };

        var path = await _writer.WriteAuditAsync(rows);
        var dataLines = (await File.ReadAllLinesAsync(path)).Skip(1).ToArray();

        // Blocked rows first (alpha+alice, alpha+bob, both blocked), then unblocked rows
        // sorted by mailbox then trustee (alpha+carol, zeta+alice).
        dataLines[0].Should().StartWith("alpha@t.com,alice@t.com");
        dataLines[1].Should().StartWith("alpha@t.com,bob@t.com");
        dataLines[2].Should().StartWith("alpha@t.com,carol@t.com");
        dataLines[3].Should().StartWith("zeta@t.com,alice@t.com");
    }

    [Fact]
    public async Task WriteAuditAsync_EscapesValuesContainingCommas()
    {
        var rows = new[]
        {
            Report("a@t.com", "u@t.com",
                signInBlocked: false, fullAccess: true, sendAs: false, sendOnBehalf: false,
                lookupStatus: UserLookupStatus.Ok, accountEnabled: true, sendAsScanned: true)
                with { DisplayName = "Doe, Jane" },
        };

        var path = await _writer.WriteAuditAsync(rows);
        var dataLine = (await File.ReadAllLinesAsync(path)).Skip(1).First();

        dataLine.Should().Contain("\"Doe, Jane\"");
    }

    [Fact]
    public async Task WriteAuditAsync_EscapesEmbeddedQuotes()
    {
        var rows = new[]
        {
            Report("a@t.com", "u@t.com",
                signInBlocked: false, fullAccess: true, sendAs: false, sendOnBehalf: false,
                lookupStatus: UserLookupStatus.Ok, accountEnabled: true, sendAsScanned: true)
                with { DisplayName = "Bob \"the Builder\"" },
        };

        var path = await _writer.WriteAuditAsync(rows);
        var dataLine = (await File.ReadAllLinesAsync(path)).Skip(1).First();

        // RFC 4180: a quote inside a quoted field is doubled.
        dataLine.Should().Contain("\"Bob \"\"the Builder\"\"\"");
    }

    // -----------------------------------------------------------------------
    // WriteCleanupAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteCleanupAsync_HeaderMatchesScriptOutput()
    {
        var path = await _writer.WriteCleanupAsync(Array.Empty<CleanupAction>());
        ReadHeaderLine(path).Should().Be("Mailbox,Trustee,Action,Result,Notes");
    }

    [Theory]
    [InlineData(AccessRight.FullAccess,   "RemoveFullAccess")]
    [InlineData(AccessRight.SendAs,       "RemoveSendAs")]
    [InlineData(AccessRight.SendOnBehalf, "RemoveSendOnBehalf")]
    public async Task WriteCleanupAsync_ActionLabelsMatchScriptStrings(
        AccessRight right, string expectedLabel)
    {
        var rows = new[]
        {
            new CleanupAction("a@t.com", "u@t.com", right, ActionResult.Success, Notes: null),
        };

        var path = await _writer.WriteCleanupAsync(rows);
        var dataLine = (await File.ReadAllLinesAsync(path)).Skip(1).First();

        dataLine.Should().Contain($",{expectedLabel},");
    }

    [Fact]
    public async Task WriteCleanupAsync_FilenameStartsWithMailboxCleanup()
    {
        var path = await _writer.WriteCleanupAsync(Array.Empty<CleanupAction>());
        Path.GetFileName(path).Should().StartWith("mailbox-cleanup-").And.EndWith(".csv");
    }

    // -----------------------------------------------------------------------
    // WriteBulkAddAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WriteBulkAddAsync_HeaderMatchesScriptOutput()
    {
        var path = await _writer.WriteBulkAddAsync(Array.Empty<BulkAddResult>());
        ReadHeaderLine(path).Should().Be(
            "User_UPN,Access_Status,SendAs_Status,Shared_Mailbox_Address");
    }

    [Fact]
    public async Task WriteBulkAddAsync_FilenameStartsWithSharedMailBulkAction()
    {
        var path = await _writer.WriteBulkAddAsync(Array.Empty<BulkAddResult>());
        Path.GetFileName(path).Should().StartWith("SharedMail-BulkAction-").And.EndWith(".csv");
    }

    [Fact]
    public async Task WriteBulkAddAsync_PrefersExplicitStatusMessagesOverOutcomeLabels()
    {
        var rows = new[]
        {
            new BulkAddResult(
                UserUpn: "u@t.com",
                SharedMailboxAddress: "a@t.com",
                FullAccessOutcome: PermissionOutcome.Granted,
                SendAsOutcome: PermissionOutcome.AlreadyPresent,
                AccessStatusMessage: "FullAccess granted",
                SendAsStatusMessage: "SendAs already present"),
        };

        var path = await _writer.WriteBulkAddAsync(rows);
        var dataLine = (await File.ReadAllLinesAsync(path)).Skip(1).First();

        dataLine.Should().Contain("FullAccess granted").And.Contain("SendAs already present");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static DelegateReport Report(
        string mailbox,
        string trustee,
        bool? signInBlocked = null,
        bool fullAccess = false,
        bool sendAs = false,
        bool sendOnBehalf = false,
        UserLookupStatus lookupStatus = UserLookupStatus.Ok,
        bool? accountEnabled = null,
        bool sendAsScanned = true) =>
        new()
        {
            Mailbox = mailbox,
            Trustee = trustee,
            SignInBlocked = signInBlocked,
            FullAccess = fullAccess,
            SendAs = sendAs,
            SendOnBehalf = sendOnBehalf,
            LookupStatus = lookupStatus,
            AccountEnabled = accountEnabled,
            SendAsScanned = sendAsScanned,
        };

    private static string ReadHeaderLine(string path)
    {
        // ReadAllLines auto-detects the UTF-8 BOM and strips it, so the returned
        // first line is the clean header text.
        return File.ReadAllLines(path, Encoding.UTF8).First();
    }
}
