using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Configuration;
using SharedMailbox.Core.Services;
using SharedMailbox.PowerShell.Hosting;

namespace SharedMailbox.PowerShell.Adapters;

/// <summary>
/// Default <see cref="IConnectionService"/>. Uses delegated access tokens acquired by an
/// <see cref="IAccessTokenProvider"/> (MSAL) to drive Connect-MgGraph and Connect-ExchangeOnline
/// without opening their built-in interactive prompts. The user therefore sees exactly one
/// system-browser sign-in per session (Graph), and the same identity is silently reused for EXO.
///
/// Flow:
///   1. IAccessTokenProvider.GetGraphTokenAsync — interactive on first launch, silent thereafter.
///   2. Connect-MgGraph -AccessToken (as SecureString, the cmdlet's required type).
///   3. IAccessTokenProvider.GetExchangeTokenAsync — silent, reusing the same account.
///   4. Connect-ExchangeOnline -AccessToken (plain string) -UserPrincipalName.
///
/// Each Connect-* is skipped if the runspace already shows a live session, mirroring the
/// original script's Get-ConnectionInformation / Get-MgContext guard.
/// </summary>
public sealed class PowerShellConnectionService : IConnectionService
{
    private readonly IPowerShellHost _host;
    private readonly IAccessTokenProvider _tokenProvider;
    private readonly AzureAdConfig _azureAd;
    private readonly ILogger<PowerShellConnectionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ConnectionStatus _status = ConnectionStatus.Disconnected;

    public PowerShellConnectionService(
        IPowerShellHost host,
        IAccessTokenProvider tokenProvider,
        AzureAdConfig azureAd,
        ILogger<PowerShellConnectionService> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _azureAd = azureAd ?? throw new ArgumentNullException(nameof(azureAd));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ConnectionStatus Status => _status;

    public event EventHandler<ConnectionStatus>? StatusChanged;

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Beginning sign-in");
            await _host.InitializeAsync(cancellationToken).ConfigureAwait(false);

            // 1. Acquire the Graph token. First-time call opens the system browser; subsequent
            //    launches resolve silently from the persisted MSAL cache.
            _logger.LogInformation("Acquiring Microsoft Graph token via MSAL");
            var graphToken = await _tokenProvider.GetGraphTokenAsync(cancellationToken).ConfigureAwait(false);

            // 2. Connect-MgGraph using the token. Connect-MgGraph -AccessToken requires SecureString
            //    in module 2.x; we convert inside the script so the plaintext doesn't sit in a
            //    .NET string longer than necessary on the PS side.
            if (!await IsGraphConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Calling Connect-MgGraph with delegated token");
                await _host.InvokeAsync(@"
                    $secure = ConvertTo-SecureString -String $Token -AsPlainText -Force
                    Connect-MgGraph -AccessToken $secure -NoWelcome -ErrorAction Stop | Out-Null",
                    new Dictionary<string, object?> { ["Token"] = graphToken.AccessToken },
                    streams: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug("Get-MgContext reports an existing Graph session; skipping Connect-MgGraph");
            }

            // 3. Acquire the Exchange token. Silent-only — reuses the account just signed in.
            _logger.LogInformation("Acquiring Exchange Online token via MSAL (silent)");
            var exoToken = await _tokenProvider.GetExchangeTokenAsync(cancellationToken).ConfigureAwait(false);

            // 4. Connect-ExchangeOnline using the token. -AccessToken is a plain string here
            //    (unlike Connect-MgGraph), and -UserPrincipalName lets EXO resolve the tenant
            //    from the token claims without an extra -Organization parameter.
            if (!await IsExoConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Calling Connect-ExchangeOnline with delegated token for {Upn}",
                    exoToken.Account.UserPrincipalName);
                await _host.InvokeAsync(
                    "Connect-ExchangeOnline -AccessToken $Token -UserPrincipalName $Upn -ShowBanner:$false -ErrorAction Stop | Out-Null",
                    new Dictionary<string, object?>
                    {
                        ["Token"] = exoToken.AccessToken,
                        ["Upn"]   = exoToken.Account.UserPrincipalName,
                    },
                    streams: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug("Get-ConnectionInformation reports an existing EXO session; skipping Connect-ExchangeOnline");
            }

            _status = new ConnectionStatus(
                ExchangeOnlineConnected: true,
                GraphConnected: true,
                SignedInUser: exoToken.Account.UserPrincipalName,
                TenantId: exoToken.Account.TenantId);

            _logger.LogInformation("Signed in as {User} (tenant {Tenant})",
                _status.SignedInUser, _status.TenantId);
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke(this, _status);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Best-effort disconnect — swallow PS errors because either side may already be gone
            // (token expired, manual disconnect from another tool, etc.). We always end Disconnected.
            try
            {
                await _host.InvokeAsync(
                    "Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue",
                    parameters: null, streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (PowerShellInvocationException ex)
            {
                _logger.LogDebug(ex, "Disconnect-ExchangeOnline failed (likely already disconnected)");
            }

            try
            {
                await _host.InvokeAsync(
                    "Disconnect-MgGraph -ErrorAction SilentlyContinue",
                    parameters: null, streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (PowerShellInvocationException ex)
            {
                _logger.LogDebug(ex, "Disconnect-MgGraph failed (likely already disconnected)");
            }

            // Clear the persistent MSAL cache so the next sign-in is fully fresh.
            try
            {
                await _tokenProvider.SignOutAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Clearing MSAL token cache failed");
            }

            _status = ConnectionStatus.Disconnected;
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke(this, _status);
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private async Task<bool> IsExoConnectedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _host.InvokeAsync(
                "Get-ConnectionInformation -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Connected' }",
                parameters: null, streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            return output.Count > 0;
        }
        catch (PowerShellInvocationException)
        {
            return false;
        }
    }

    private async Task<bool> IsGraphConnectedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await _host.InvokeAsync(
                "Get-MgContext -ErrorAction SilentlyContinue",
                parameters: null, streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            return output.Count > 0 && output[0] is not null;
        }
        catch (PowerShellInvocationException)
        {
            return false;
        }
    }
}
