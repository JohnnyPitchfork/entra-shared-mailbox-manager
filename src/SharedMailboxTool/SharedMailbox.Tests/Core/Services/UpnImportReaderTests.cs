using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMailbox.Core.Services;

namespace SharedMailbox.Tests.Core.Services;

/// <summary>
/// Tests for <see cref="UpnImportReader"/>. Covers the happy path plus the two
/// rejection cases the original PowerShell script's import block enforced:
///   * Missing 'UPN' column → UpnImportException.
///   * No usable values in the 'UPN' column → UpnImportException.
/// </summary>
public class UpnImportReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly UpnImportReader _reader;

    public UpnImportReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"smt-upn-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _reader = new UpnImportReader(NullLogger<UpnImportReader>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* swallow */ }
    }

    // -----------------------------------------------------------------------
    // Happy paths
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_SingleColumnFile_ReturnsAllUpns()
    {
        var path = await WriteCsvAsync("UPN\nalice@t.com\nbob@t.com\ncarol@t.com\n");

        var result = await _reader.ReadAsync(path);

        result.Should().Equal("alice@t.com", "bob@t.com", "carol@t.com");
    }

    [Fact]
    public async Task ReadAsync_TrimsLeadingAndTrailingWhitespace()
    {
        var path = await WriteCsvAsync("UPN\n  alice@t.com  \nbob@t.com\n");

        var result = await _reader.ReadAsync(path);

        result.Should().Equal("alice@t.com", "bob@t.com");
    }

    [Fact]
    public async Task ReadAsync_DedupesCaseInsensitively()
    {
        var path = await WriteCsvAsync("UPN\nAlice@t.com\nalice@t.com\nALICE@T.COM\nbob@t.com\n");

        var result = await _reader.ReadAsync(path);

        result.Should().HaveCount(2);
        result[0].Should().Be("Alice@t.com");  // first occurrence wins
        result[1].Should().Be("bob@t.com");
    }

    [Fact]
    public async Task ReadAsync_SkipsEmptyRows()
    {
        var path = await WriteCsvAsync("UPN\nalice@t.com\n\n   \nbob@t.com\n");

        var result = await _reader.ReadAsync(path);

        result.Should().Equal("alice@t.com", "bob@t.com");
    }

    [Fact]
    public async Task ReadAsync_HeaderNameIsCaseInsensitive()
    {
        var path = await WriteCsvAsync("upn\nalice@t.com\nbob@t.com\n");

        var result = await _reader.ReadAsync(path);

        result.Should().Equal("alice@t.com", "bob@t.com");
    }

    [Fact]
    public async Task ReadAsync_MultiColumnFile_PicksUpnColumnRegardlessOfPosition()
    {
        // UPN is the third column. Reader must still find it by name, not position.
        var path = await WriteCsvAsync(
            "FirstName,LastName,UPN,Department\nAlice,A,alice@t.com,IT\nBob,B,bob@t.com,Ops\n");

        var result = await _reader.ReadAsync(path);

        result.Should().Equal("alice@t.com", "bob@t.com");
    }

    [Fact]
    public async Task ReadAsync_HandlesQuotedValuesAndEmbeddedQuotes()
    {
        // RFC 4180-style escaping: embedded quotes are doubled inside a quoted field.
        var path = await WriteCsvAsync(
            "Name,UPN\n\"Doe, Jane\",jane@t.com\n\"Bob \"\"the Builder\"\"\",bob@t.com\n");

        var result = await _reader.ReadAsync(path);

        result.Should().Equal("jane@t.com", "bob@t.com");
    }

    // -----------------------------------------------------------------------
    // Rejection cases
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_FileDoesNotExist_Throws()
    {
        var missing = Path.Combine(_tempDir, "nope.csv");

        var act = async () => await _reader.ReadAsync(missing);

        await act.Should().ThrowAsync<UpnImportException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task ReadAsync_EmptyPath_Throws()
    {
        var act = async () => await _reader.ReadAsync("");

        await act.Should().ThrowAsync<UpnImportException>();
    }

    [Fact]
    public async Task ReadAsync_EmptyFile_Throws()
    {
        var path = await WriteCsvAsync("");

        var act = async () => await _reader.ReadAsync(path);

        await act.Should().ThrowAsync<UpnImportException>();
    }

    [Fact]
    public async Task ReadAsync_MissingUpnColumn_Throws()
    {
        var path = await WriteCsvAsync("Email,Name\nalice@t.com,Alice\n");

        var act = async () => await _reader.ReadAsync(path);

        await act.Should().ThrowAsync<UpnImportException>()
            .WithMessage("*'UPN'*");
    }

    [Fact]
    public async Task ReadAsync_HeaderPresentButColumnAllEmpty_Throws()
    {
        var path = await WriteCsvAsync("UPN\n\n   \n\n");

        var act = async () => await _reader.ReadAsync(path);

        await act.Should().ThrowAsync<UpnImportException>()
            .WithMessage("*No valid UPNs*");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<string> WriteCsvAsync(string content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, content);
        return path;
    }
}
