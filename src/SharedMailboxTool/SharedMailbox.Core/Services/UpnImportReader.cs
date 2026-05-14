using Microsoft.Extensions.Logging;

namespace SharedMailbox.Core.Services;

/// <summary>
/// Default <see cref="IUpnImportReader"/>. Reads a CSV with a header row that must
/// contain a 'UPN' column, returns unique trimmed non-empty UPNs in encounter order.
///
/// Faithful to the PS script's import block:
///   * Header check: CSV must contain a column named 'UPN' (case-insensitive).
///   * Value cleanup: trim whitespace, drop empties, dedupe.
///   * Errors raised as <see cref="UpnImportException"/> for the caller to surface
///     in the UI (in the script these were Write-Host red lines + a re-prompt loop).
/// </summary>
public sealed class UpnImportReader : IUpnImportReader
{
    private readonly ILogger<UpnImportReader> _logger;

    public UpnImportReader(ILogger<UpnImportReader> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<string>> ReadAsync(string csvPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(csvPath))
            throw new UpnImportException("No file path provided.");

        if (!File.Exists(csvPath))
            throw new UpnImportException($"File not found at '{csvPath}'.");

        _logger.LogInformation("Reading UPN import from {Path}", csvPath);

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(csvPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new UpnImportException($"Failed to read CSV file: {ex.Message}", ex);
        }

        if (lines.Length == 0)
            throw new UpnImportException("CSV is empty.");

        var headers = SplitCsvRow(lines[0]);
        var upnColumn = Array.FindIndex(headers, h => string.Equals(h.Trim(), "UPN", StringComparison.OrdinalIgnoreCase));

        if (upnColumn < 0)
            throw new UpnImportException("CSV must contain a column named 'UPN'.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        for (var i = 1; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cells = SplitCsvRow(lines[i]);
            if (cells.Length <= upnColumn) continue;

            var upn = cells[upnColumn].Trim();
            if (string.IsNullOrEmpty(upn)) continue;
            if (!seen.Add(upn)) continue;

            result.Add(upn);
        }

        if (result.Count == 0)
            throw new UpnImportException("No valid UPNs found in the 'UPN' column.");

        _logger.LogInformation("Imported {Count} UPN(s) from {Path}", result.Count, csvPath);
        return result;
    }

    /// <summary>
    /// Minimal RFC-4180-style splitter for one CSV row. Handles quoted fields with
    /// embedded commas and doubled quotes ("). Sufficient for the UPN import case;
    /// we don't try to handle multi-line quoted cells because they don't appear in
    /// these files.
    /// </summary>
    private static string[] SplitCsvRow(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                switch (c)
                {
                    case ',':
                        cells.Add(sb.ToString());
                        sb.Clear();
                        break;
                    case '"':
                        inQuotes = true;
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
        }

        cells.Add(sb.ToString());
        return cells.ToArray();
    }
}
