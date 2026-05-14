using System.Windows;
using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Services;

namespace SharedMailbox.App;

/// <summary>
/// Placeholder shell for the v2 application. Owns nothing but a sign-in / sign-out
/// pair of buttons and a status line so we can verify the MSAL + PowerShell host
/// stack works end-to-end before any real view is written.
///
/// In the next phase the body of this window is replaced by a navigation host
/// (group picker -> mailbox list -> audit / cleanup / bulk-grant flows), and the
/// code-behind here shrinks back to just <c>InitializeComponent</c> while the
/// actions move into ViewModels.
/// </summary>
public partial class MainWindow : Window
{
    private readonly IConnectionService _connectionService;
    private readonly ILogger<MainWindow> _logger;

    public MainWindow(IConnectionService connectionService, ILogger<MainWindow> logger)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeComponent();

        _connectionService.StatusChanged += OnConnectionStatusChanged;
        ApplyStatus(_connectionService.Status);
    }

    private async void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ClearError();

        try
        {
            await _connectionService.SignInAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-in failed");
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SignOutButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ClearError();

        try
        {
            await _connectionService.SignOutAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-out failed");
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // -----------------------------------------------------------------------
    // Status / error rendering
    // -----------------------------------------------------------------------

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        // The event may fire on any thread (the service is awaited from one).
        // Marshal back to the UI dispatcher before touching XAML elements.
        Dispatcher.Invoke(() => ApplyStatus(status));
    }

    private void ApplyStatus(ConnectionStatus status)
    {
        if (status.IsFullyConnected)
        {
            StatusText.Text = $"Signed in as {status.SignedInUser ?? "(unknown)"} " +
                              $"(tenant {status.TenantId ?? "(unknown)"}).";
            SignInButton.IsEnabled = false;
            SignOutButton.IsEnabled = true;
        }
        else
        {
            StatusText.Text = "Not signed in.";
            SignInButton.IsEnabled = true;
            SignOutButton.IsEnabled = false;
        }
    }

    private void SetBusy(bool busy)
    {
        // Disable both buttons while an operation is in flight. The status-changed
        // event will re-enable the correct one when the action completes.
        SignInButton.IsEnabled = !busy && !_connectionService.Status.IsFullyConnected;
        SignOutButton.IsEnabled = !busy && _connectionService.Status.IsFullyConnected;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void ShowError(Exception ex)
    {
        ErrorText.Text = $"{ex.GetType().Name}: {ex.Message}";
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }
}
