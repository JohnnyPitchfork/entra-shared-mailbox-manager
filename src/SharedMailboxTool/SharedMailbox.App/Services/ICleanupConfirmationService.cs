using SharedMailbox.App.ViewModels;
using SharedMailbox.App.Views;
using SharedMailbox.Core.Domain;

namespace SharedMailbox.App.Services;

/// <summary>
/// Shows a modal confirmation dialog listing the exact permissions about to be revoked
/// and returns true only if the user clicks the "Remove permissions" button. Used by
/// <see cref="CleanupViewModel"/> to gate destructive operations without coupling the
/// view model to a specific dialog class.
///
/// Sync (blocking) by design — the caller is on the UI thread when the command fires
/// and <see cref="System.Windows.Window.ShowDialog"/> runs its own message pump.
/// </summary>
public interface ICleanupConfirmationService
{
    bool Confirm(IReadOnlyList<DelegateReport> rowsToRemove);
}

/// <summary>
/// Default <see cref="ICleanupConfirmationService"/>. Owns the dialog window's lifecycle:
/// constructs the window with its VM, parents it to the application's main window so it
/// centers correctly and behaves modally, and translates the dialog's bool? result into
/// a clear true/false.
/// </summary>
public sealed class CleanupConfirmationService : ICleanupConfirmationService
{
    public bool Confirm(IReadOnlyList<DelegateReport> rowsToRemove)
    {
        ArgumentNullException.ThrowIfNull(rowsToRemove);

        var vm = new CleanupConfirmDialogViewModel(rowsToRemove);
        var dialog = new CleanupConfirmDialog(vm)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true;
    }
}
