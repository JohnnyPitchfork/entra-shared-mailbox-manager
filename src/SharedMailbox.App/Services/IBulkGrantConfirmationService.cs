using SharedMailbox.App.ViewModels;
using SharedMailbox.App.Views;

namespace SharedMailbox.App.Services;

/// <summary>
/// Shows a modal confirmation dialog summarizing the bulk grant about to run and
/// returns true only if the user clicks "Grant access" in the dialog. Used by
/// <see cref="BulkGrantViewModel"/> to gate the destructive operation without
/// coupling the VM to a specific window class.
///
/// Sync (blocking) by design — same rationale as <see cref="ICleanupConfirmationService"/>.
/// </summary>
public interface IBulkGrantConfirmationService
{
    bool Confirm(IReadOnlyList<string> userUpns, IReadOnlyList<string> mailboxUpns, bool grantSendAs);
}

/// <summary>
/// Default <see cref="IBulkGrantConfirmationService"/>. Builds the dialog VM, parents
/// the window to the application's main window for correct modality, and translates
/// the dialog's bool? result into a clean true/false.
/// </summary>
public sealed class BulkGrantConfirmationService : IBulkGrantConfirmationService
{
    public bool Confirm(IReadOnlyList<string> userUpns, IReadOnlyList<string> mailboxUpns, bool grantSendAs)
    {
        ArgumentNullException.ThrowIfNull(userUpns);
        ArgumentNullException.ThrowIfNull(mailboxUpns);

        var vm = new BulkGrantConfirmDialogViewModel(userUpns, mailboxUpns, grantSendAs);
        var dialog = new BulkGrantConfirmDialog(vm)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true;
    }
}
