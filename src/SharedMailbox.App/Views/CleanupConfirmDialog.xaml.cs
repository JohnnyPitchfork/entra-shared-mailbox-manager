using System.Windows;
using SharedMailbox.App.ViewModels;
using Wpf.Ui.Controls;

namespace SharedMailbox.App.Views;

/// <summary>
/// Modal confirmation window shown before any cleanup Remove-* runs against EXO.
/// Bound to a <see cref="CleanupConfirmDialogViewModel"/>; the buttons set
/// <see cref="Window.DialogResult"/> and close. The opening service inspects
/// <c>ShowDialog() == true</c> to decide whether to proceed.
///
/// Visually a smaller FluentWindow so it picks up the same chrome / theming as the
/// shell window when shown as a child dialog.
/// </summary>
public partial class CleanupConfirmDialog : FluentWindow
{
    public CleanupConfirmDialog(CleanupConfirmDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
