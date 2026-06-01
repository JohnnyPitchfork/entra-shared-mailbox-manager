using SharedMailbox.Core.Domain;

namespace SharedMailbox.App.ViewModels;

/// <summary>
/// View model for <c>CleanupConfirmDialog</c>. Flattens the selected rows into a
/// human-readable list of <see cref="ConfirmItem"/> entries the dialog's DataGrid
/// binds to, plus a title + subtitle for the dialog header.
/// </summary>
public sealed class CleanupConfirmDialogViewModel
{
    public CleanupConfirmDialogViewModel(IReadOnlyList<DelegateReport> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Items = rows
            .Select(r => new ConfirmItem(
                Mailbox: r.Mailbox,
                Trustee: r.Trustee,
                DisplayName: r.DisplayName,
                RightsToRemove: FormatRights(r)))
            .ToList();

        Title = Items.Count == 1
            ? "Remove permissions for 1 trustee?"
            : $"Remove permissions for {Items.Count} trustees?";

        Subtitle = "This action is irreversible and runs against Exchange Online immediately. " +
                   "Review the list below carefully — these are the exact permissions that will be revoked.";
    }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<ConfirmItem> Items { get; }

    private static string FormatRights(DelegateReport r)
    {
        var rights = new List<string>(3);
        if (r.FullAccess)   rights.Add("FullAccess");
        if (r.SendAs)       rights.Add("SendAs");
        if (r.SendOnBehalf) rights.Add("SendOnBehalf");
        return rights.Count == 0 ? "(none)" : string.Join(", ", rights);
    }

    /// <summary>One row in the dialog's confirmation grid.</summary>
    public sealed record ConfirmItem(
        string Mailbox,
        string Trustee,
        string? DisplayName,
        string RightsToRemove);
}
