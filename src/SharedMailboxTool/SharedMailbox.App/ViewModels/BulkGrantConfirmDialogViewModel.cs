namespace SharedMailbox.App.ViewModels;

/// <summary>
/// View model for <c>BulkGrantConfirmDialog</c>. Shows the user the two lists they're
/// about to fan-out across (users × mailboxes) before any Add-* cmdlet runs.
///
/// We display the lists separately rather than the full cartesian product because the
/// dialog would otherwise show N×M rows for what is conceptually just two inputs —
/// 5 users × 30 mailboxes = 150 rows is noisy and not actionable.
/// </summary>
public sealed class BulkGrantConfirmDialogViewModel
{
    public BulkGrantConfirmDialogViewModel(
        IReadOnlyList<string> userUpns,
        IReadOnlyList<string> mailboxUpns,
        bool grantSendAs)
    {
        ArgumentNullException.ThrowIfNull(userUpns);
        ArgumentNullException.ThrowIfNull(mailboxUpns);

        Users = userUpns.ToList();
        Mailboxes = mailboxUpns.ToList();
        GrantSendAs = grantSendAs;

        var rights = grantSendAs ? "FullAccess + SendAs" : "FullAccess";
        var totalOps = userUpns.Count * mailboxUpns.Count;

        Title = $"Grant {rights} to {Pluralize(userUpns.Count, "user")} " +
                $"across {Pluralize(mailboxUpns.Count, "mailbox", "mailboxes")}?";

        Subtitle =
            $"{totalOps} (user × mailbox) operation(s) will run against Exchange Online. " +
            "Permissions the user already holds are skipped automatically. " +
            "Every attempt is recorded to the bulk-action CSV regardless of outcome.";
    }

    public string Title { get; }
    public string Subtitle { get; }
    public IReadOnlyList<string> Users { get; }
    public IReadOnlyList<string> Mailboxes { get; }
    public bool GrantSendAs { get; }

    public string UsersHeader => $"Users ({Users.Count})";
    public string MailboxesHeader => $"Mailboxes ({Mailboxes.Count})";

    private static string Pluralize(int count, string singular, string? plural = null) =>
        count == 1 ? $"1 {singular}" : $"{count} {plural ?? singular + "s"}";
}
