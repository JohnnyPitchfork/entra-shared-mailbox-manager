using System.Windows.Controls;

namespace SharedMailbox.App.Views;

/// <summary>
/// Cleanup-tab content. All logic lives in <see cref="ViewModels.CleanupViewModel"/>;
/// DataContext is set by the parent window via <c>DataContext="{Binding Cleanup}"</c>.
/// </summary>
public partial class CleanupView : UserControl
{
    public CleanupView()
    {
        InitializeComponent();
    }
}
