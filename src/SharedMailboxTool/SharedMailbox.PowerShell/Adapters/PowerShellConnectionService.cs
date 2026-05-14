using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Configuration;
using SharedMailbox.Core.Services;
using SharedMailbox.PowerShell.Hosting;

namespace SharedMailbox.PowerShell.Adapters;

/// <summary>
/// Default <see cref="IConnectionService"/>. Drives Connect-ExchangeOnline and Connect-MgGraph
/// the same way the original script does — interactive browser prompt — and remembers the
/// resulting session info so the UI can show "Signed in as user@tenant".
///
/// Phase 2 (after MSAL is wired up in the App project): replace the interactive Connect-* calls
/// with token-based connects, using a delegated AccessToken acquired via MSAL public client.
/// This removes the second browser popup on EXO and gives us silent token refresh between
/// sessions. The interface stays the same — only this class changes.
/// </summary>
public sealed class PowerShellConnectionService : IConnectionService
{
    private readonly IPowerShellHost _host;
    private readonly AzureAdConfig _azureAd;
    private readonly ILogger<PowerShellConnectionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ConnectionStatus _status = ConnectionStatus.Disconnected;

    public PowerShellConnectionService(
        IPowerShellHost host,
        AzureAdConfig azureAd,
        ILogger<PowerShellConnectionService> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
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

            // 1. Exchange Online — skip if a session already exists, matches the script's
            //    `Get-ConnectionInformation | Where-Object Name -eq 'ExchangeOnline_1'` check.
            var exoAlreadyConnected = await IsExoConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (!exoAlreadyConnected)
            {
                _logger.LogInformation("Calling Connect-ExchangeOnline");
                await _host.InvokeAsync(
                    "Connect-ExchangeOnline -ShowBanner:$false -ErrorAction Stop",
                    parameters: null,
                    streams: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // 2. Microsoft Graph — skip if Get-MgContext returns non-null, matches the script.
            var graphAlreadyConnected = await IsGraphConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (!graphAlreadyConnected)
            {
                _logger.LogInformation("Calling Connect-MgGraph with scopes {Scopes}", string.Join(",", _azureAd.GraphScopes));
                await _host.InvokeAsync(
                    "Connect-MgGraph -Scopes $Scopes -NoWelcome -ErrorAction Stop",
                    new Dictionary<string, object?> { ["Scopes"] = _azureAd.GraphScopes.ToArray() },
                    streams: null,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            // 3. Resolve the signed-in identity for display in the UI.
            _status = await ProbeStatusAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Signed in as {User} (tenant {Tenant})", _status.SignedInUser, _status.TenantId);
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
            // Best-effort disconnect — swallow errors because either side may already be
            // gone (token expired, manual disconnect, etc.). We always end at Disconnected.
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

    private async Task<ConnectionStatus> ProbeStatusAsync(CancellationToken cancellationToken)
    {
        string? signedInUser = null;
        string? tenantId = null;

        // Get-MgContext gives us the Graph-side identity.
        try
        {
            var ctx = await _host.InvokeAsync(
                "Get-MgContext",
                parameters: null, streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (ctx.Count > 0)
            {
                signedInUser = ctx[0].Properties["Account"]?.Value?.ToString();
                tenantId = ctx[0].Properties["TenantId"]?.Value?.ToString();
            }
        }
        catch (PowerShellInvocationException ex)
        {
            _logger.LogDebug(ex, "Get-MgContext probe failed");
        }

        // If Graph didn't give us the user, fall back to the EXO connection info.
        if (signedInUser is null)
        {
            try
            {
                var info = await _host.InvokeAsync(
                    "Get-ConnectionInformation | Select-Object -First 1",
                    parameters: null, streams: null, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (info.Count > 0)
                {
                    signedInUser ??= info[0].Properties["UserPrincipalName"]?.Value?.ToString();
                    tenantId ??= info[0].Properties["TenantId"]?.Value?.ToString();
                }
            }
            catch (PowerShellInvocationException ex)
            {
                _logger.LogDebug(ex, "Get-ConnectionInformation probe failed");
            }
        }

        return new ConnectionStatus(
            ExchangeOnlineConnected: true,
            GraphConnected: true,
            SignedInUser: signedInUser,
            TenantId: tenantId);
    }
}
