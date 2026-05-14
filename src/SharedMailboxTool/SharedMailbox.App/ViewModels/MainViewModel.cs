using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Services;

namespace SharedMailbox.App.ViewModels;

/// <summary>
/// Top-level view model for <c>MainWindow</c>. Owns:
///   * The connection state surfaced in the sidebar (Signed in as / Sign in button).
///   * The <see cref="GroupPickerViewModel"/> the sidebar binds to.
///   * (Future) The three tab view models (Audit / Cleanup / Bulk Grant) added in batches 5–7.
///
/// All three sign-in / sign-out commands run async; busy state disables both commands during
/// an in-flight operation, and the <c>StatusChanged</c> subscription marshals back to the
/// UI dispatcher so the sidebar updates without explicit Invoke calls in the view.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IConnectionService _connectionService;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        IConnectionService connectionService,
        GroupPickerViewModel groupPicker,
        AuditViewModel audit,
        ILogger<MainViewModel> logger)
    {
        _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        GroupPicker = groupPicker ?? throw new ArgumentNullException(nameof(groupPicker));
        Audit = audit ?? throw new ArgumentNullException(nameof(audit));

        _connectionService.StatusChanged += OnConnectionStatusChanged;
        SyncStatus(_connectionService.Status);
    }

    public GroupPickerViewModel GroupPicker { get; }

    /// <summary>Audit-tab view model. Bound by MainWindow.xaml to the Audit tab's content.</summary>
    public AuditViewModel Audit { get; }

    [ObservableProperty]
    private string _statusText = "Not signed in.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    private bool _isSignedIn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignOutCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasError;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        ClearError();
        try
        {
            await _connectionService.SignInAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-in failed");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOutAsync()
    {
        IsBusy = true;
        ClearError();
        try
        {
            await _connectionService.SignOutAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-out failed");
            SetError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSignIn() => !IsSignedIn && !IsBusy;
    private bool CanSignOut() => IsSignedIn && !IsBusy;

    // -----------------------------------------------------------------------
    // Status synchronization
    // -----------------------------------------------------------------------

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status)
    {
        // The connection service may raise StatusChanged from a non-UI thread (whichever
        // thread the SignIn/SignOut task continuation ran on). Marshal to the WPF
        // dispatcher before touching observable properties — bound XAML expects updates
        // on the UI thread.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SyncStatus(status);
        }
        else
        {
            dispatcher.Invoke(() => SyncStatus(status));
        }
    }

    private void SyncStatus(ConnectionStatus status)
    {
        if (status.IsFullyConnected)
        {
            StatusText = $"Signed in as {status.SignedInUser ?? "(unknown)"}";
            IsSignedIn = true;
        }
        else
        {
            StatusText = "Not signed in.";
            IsSignedIn = false;
        }
    }

    private void SetError(Exception ex)
    {
        ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
        HasError = true;
    }

    private void ClearError()
    {
        ErrorMessage = null;
        HasError = false;
    }
}
