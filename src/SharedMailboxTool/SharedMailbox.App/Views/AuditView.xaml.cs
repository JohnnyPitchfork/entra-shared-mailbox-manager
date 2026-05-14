using System.Windows.Controls;

namespace SharedMailbox.App.Views;

/// <summary>
/// Audit-tab content. All logic lives in <see cref="ViewModels.AuditViewModel"/>; the
/// view's DataContext is set by the parent window via <c>DataContext="{Binding Audit}"</c>
/// in <c>MainWindow.xaml</c>, so this code-behind only needs to call InitializeComponent.
/// </summary>
public partial class AuditView : UserControl
{
    public AuditView()
    {
        InitializeComponent();
    }
}
