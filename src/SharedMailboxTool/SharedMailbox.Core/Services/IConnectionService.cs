namespace SharedMailbox.Core.Services;

/// <summary>
/// Owns the state machine for connecting to Exchange Online and Microsoft Graph.
/// The view model subscribes to <see cref="StatusChanged"/> to show "Sign in" vs
/// "Signed in as user@tenant" and to gate the action buttons.
///
/// Implementation responsibilities:
///   - Acquire a delegated token via MSAL (interactive on first sign-in, silent on resume).
///   - Run Connect-ExchangeOnline with that token (UserPrincipalName + AccessToken).
///   - Run Connect-MgGraph with the same token (or independent if scopes differ).
///   - Skip both connect calls when already connected (matches the script's
///     Get-ConnectionInformation / Get-MgContext guard).
/// </summary>
public interface IConnectionService
{
    ConnectionStatus Status { get; }

    event EventHandler<ConnectionStatus>? StatusChanged;

    Task SignInAsync(CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);
}

public sealed record ConnectionStatus(
    bool ExchangeOnlineConnected,
    bool GraphConnected,
    string? SignedInUser,
    string? TenantId)
{
    public static ConnectionStatus Disconnected { get; } = new(false, false, null, null);

    public bool IsFullyConnected => ExchangeOnlineConnected && GraphConnected;
}
