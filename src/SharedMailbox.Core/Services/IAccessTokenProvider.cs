namespace SharedMailbox.Core.Services;

/// <summary>
/// Acquires Microsoft identity-platform access tokens for the two resources this app
/// talks to: Microsoft Graph and Exchange Online.
///
/// Implementations encapsulate MSAL details (PublicClientApplication, token cache
/// persistence, broker integration). Adapters in <c>SharedMailbox.PowerShell</c> and
/// elsewhere consume only this interface so they don't take an MSAL dependency.
///
/// Contract:
///   * Both Get*TokenAsync methods reuse a cached account silently when possible and
///     fall back to an interactive prompt only when no usable refresh token exists.
///   * The two methods share the same signed-in account — calling GetGraphTokenAsync
///     and then GetExchangeTokenAsync uses the same Entra identity within one process.
///   * GetExchangeTokenAsync may throw if no account is signed in (i.e., if the caller
///     has not yet called GetGraphTokenAsync). Callers should always acquire the Graph
///     token first.
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>The account currently signed in to the token cache, if any.</summary>
    AccessTokenAccount? CurrentAccount { get; }

    /// <summary>
    /// Acquire a delegated token for Microsoft Graph. Scopes are read from configuration.
    /// May trigger an interactive sign-in prompt the first time it's called.
    /// </summary>
    Task<AccessTokenResult> GetGraphTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquire a delegated token for the Exchange Online REST endpoint. The token is
    /// passed to <c>Connect-ExchangeOnline -AccessToken</c> by the PowerShell adapter.
    /// </summary>
    Task<AccessTokenResult> GetExchangeTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>Remove all accounts from the persistent token cache.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>One successful token acquisition.</summary>
public sealed record AccessTokenResult(
    string AccessToken,
    DateTimeOffset ExpiresOn,
    AccessTokenAccount Account);

/// <summary>The user identity behind an issued token.</summary>
public sealed record AccessTokenAccount(
    string UserPrincipalName,
    string TenantId,
    string AccountObjectId);
