using System.Windows;
using SharedMailbox.App.ViewModels;
using Wpf.Ui.Controls;

namespace SharedMailbox.App.Views;

/// <summary>
/// Modal confirmation window shown before any Add-* cmdlet runs against EXO.
/// Bound to a <see cref="BulkGrantConfirmDialogViewModel"/>; the buttons set
/// <see cref="Window.DialogResult"/> and close. The opening service inspects
/// <c>ShowDialog() == true</c> to decide whether to proceed.
/// </summary>
public partial class BulkGrantConfirmDialog : FluentWindow
{
    public BulkGrantConfirmDialog(BulkGrantConfirmDialogViewModel viewModel)
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
