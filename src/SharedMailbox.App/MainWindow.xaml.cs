using SharedMailbox.App.ViewModels;
using Wpf.Ui.Controls;

namespace SharedMailbox.App;

/// <summary>
/// The application shell. Sidebar holds sign-in status + group picker; the right pane's
/// TabControl hosts the three flow views (Audit / Cleanup / Bulk Grant) — currently
/// placeholders, replaced by real views in batches 5–7.
///
/// All behaviour lives in <see cref="MainViewModel"/>. This code-behind exists only to
/// satisfy WPF's partial-class contract and to set the DataContext from the DI-resolved
/// view model — no event handlers, no logic.
///
/// Base class is <see cref="FluentWindow"/> (from WPF-UI) rather than <see cref="System.Windows.Window"/>
/// so the window picks up Mica/acrylic-aware chrome and the Fluent control styles merged
/// from <c>App.xaml</c>'s resource dictionaries.
/// </summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }
}
