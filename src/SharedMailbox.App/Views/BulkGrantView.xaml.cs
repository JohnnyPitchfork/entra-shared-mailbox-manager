using System.Windows.Controls;

namespace SharedMailbox.App.Views;

/// <summary>
/// Bulk-Grant-tab content. All logic lives in <see cref="ViewModels.BulkGrantViewModel"/>;
/// DataContext is set by the parent window via <c>DataContext="{Binding BulkGrant}"</c>.
/// </summary>
public partial class BulkGrantView : UserControl
{
    public BulkGrantView()
    {
        InitializeComponent();
    }
}
