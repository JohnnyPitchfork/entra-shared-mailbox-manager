using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using SharedMailbox.App.Configuration;
using SharedMailbox.Core.Configuration;
using SharedMailbox.Core.Services;

namespace SharedMailbox.App.Authentication;

/// <summary>
/// MSAL-backed <see cref="IAccessTokenProvider"/>. Builds a PublicClientApplication for
/// the Entra tenant + app reg from <see cref="AzureAdConfig"/> and persists its token
/// cache to <c>%LOCALAPPDATA%\entra-shared-mailbox-manager\msal.cache</c> via
/// <c>Microsoft.Identity.Client.Extensions.Msal</c> (DPAPI-encrypted on Windows).
///
/// Sign-in flow per Get*TokenAsync call:
///   1. AcquireTokenSilent against the cached account.
///   2. If no cached account or the cache can't refresh, AcquireTokenInteractive
///      (system browser) — but ONLY on the first call (Graph). The Exchange call
///      reuses the same account silently.
///
/// Concurrency:
///   PublicClientApplication.AcquireToken* are thread-safe; we just need to serialize
///   first-time construction (PCA + cache helper) behind a SemaphoreSlim.
/// </summary>
public sealed class MsalAccessTokenProvider : IAccessTokenProvider, IAsyncDisposable
{
    /// <summary>
    /// MSAL cache filename inside <see cref="ConfigPaths.UserDataDirectory"/>.
    /// Matches the <c>msal_cache*</c> gitignore pattern for safety; the actual file
    /// lives under LOCALAPPDATA so it would never be inside a repo anyway.
    /// </summary>
    private const string MsalCacheFileName = "msal_cache.bin";

    private readonly AzureAdConfig _config;
    private readonly ConfigPaths _paths;
    private readonly ILogger<MsalAccessTokenProvider> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);

    private IPublicClientApplication? _pca;
    private MsalCacheHelper? _cacheHelper;
    private bool _disposed;

    public MsalAccessTokenProvider(
        AzureAdConfig config,
        ConfigPaths paths,
        ILogger<MsalAccessTokenProvider> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AccessTokenAccount? CurrentAccount { get; private set; }

    public async Task<AccessTokenResult> GetGraphTokenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var scopes = _config.GraphScopes.ToArray();
        if (scopes.Length == 0)
        {
            throw new InvalidOperationException(
                "AzureAd.GraphScopes is empty. At minimum, request 'Group.Read.All' and 'User.Read.All'.");
        }

        return await AcquireAsync(scopes, allowInteractive: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AccessTokenResult> GetExchangeTokenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        // Build the Exchange .default scope from the configured resource ('https://outlook.office365.com').
        var resource = (_config.ExchangeResource ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(resource))
        {
            throw new InvalidOperationException(
                "AzureAd.ExchangeResource is empty. Expected 'https://outlook.office365.com' (or your sovereign cloud equivalent).");
        }

        var scopes = new[] { resource + "/.default" };

        // Exchange acquisition is silent-only — callers must acquire the Graph token first
        // (which is interactive). This avoids opening a second browser prompt at sign-in.
        return await AcquireAsync(scopes, allowInteractive: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var accounts = await _pca!.GetAccountsAsync().ConfigureAwait(false);
        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _pca.RemoveAsync(account).ConfigureAwait(false);
        }

        CurrentAccount = null;
        _logger.LogInformation("MSAL token cache cleared");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_cacheHelper is not null && _pca is not null)
        {
            try { _cacheHelper.UnregisterCache(_pca.UserTokenCache); }
            catch (Exception ex) { _logger.LogDebug(ex, "Unregister MSAL cache failed"); }
        }

        _initGate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pca is not null) return;

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pca is not null) return;

            _logger.LogInformation(
                "Building MSAL PublicClientApplication (tenant={TenantId}, client={ClientId})",
                _config.TenantId, _config.ClientId);

            _paths.EnsureUserDataDirectory();

            var pca = PublicClientApplicationBuilder
                .Create(_config.ClientId)
                .WithTenantId(_config.TenantId)
                .WithRedirectUri(_config.RedirectUri)
                .Build();

            // Persistent, OS-encrypted token cache so the user doesn't re-auth every launch.
            var storage = new StorageCreationPropertiesBuilder(MsalCacheFileName, _paths.UserDataDirectory)
                .Build();

            var cacheHelper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
            cacheHelper.RegisterCache(pca.UserTokenCache);

            _pca = pca;
            _cacheHelper = cacheHelper;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task<AccessTokenResult> AcquireAsync(
        string[] scopes,
        bool allowInteractive,
        CancellationToken cancellationToken)
    {
        var pca = _pca!;
        var accounts = await pca.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();

        AuthenticationResult? result = null;

        if (account is not null)
        {
            try
            {
                result = await pca
                    .AcquireTokenSilent(scopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (MsalUiRequiredException) when (allowInteractive)
            {
                _logger.LogInformation(
                    "Silent token acquisition requires user interaction; falling back to interactive prompt.");
            }
        }

        if (result is null)
        {
            if (!allowInteractive)
            {
                throw new InvalidOperationException(
                    "No cached account available for silent Exchange token acquisition. " +
                    "Call GetGraphTokenAsync first to sign the user in.");
            }

            result = await pca
                .AcquireTokenInteractive(scopes)
                .WithAccount(account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var resolved = new AccessTokenAccount(
            UserPrincipalName: result.Account.Username,
            TenantId: result.TenantId ?? _config.TenantId,
            AccountObjectId: result.Account.HomeAccountId.ObjectId);

        CurrentAccount = resolved;

        _logger.LogDebug(
            "Acquired token for scopes [{Scopes}] as {Upn}; expires {Expires:o}",
            string.Join(", ", scopes), resolved.UserPrincipalName, result.ExpiresOn);

        return new AccessTokenResult(result.AccessToken, result.ExpiresOn, resolved);
    }
}
