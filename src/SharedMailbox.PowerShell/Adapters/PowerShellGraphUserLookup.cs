using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Domain;
using SharedMailbox.Core.Services;
using SharedMailbox.PowerShell.Hosting;

namespace SharedMailbox.PowerShell.Adapters;

/// <summary>
/// Default <see cref="IGraphUserLookup"/>. Wraps Get-MgUser with an in-memory cache keyed
/// by normalized identity (lower-cased, trimmed).
///
/// Mirrors the $script:GraphUserCache + Get-UserSignInStatus pair from the original script,
/// but uses a ConcurrentDictionary so callers can fan-out lookups in parallel without
/// double-fetching the same user. The cache outlives a single audit — it lasts for as long
/// as the lookup instance is alive (typically: one app session, cleared on sign-out).
/// </summary>
public sealed class PowerShellGraphUserLookup : IGraphUserLookup
{
    private readonly IPowerShellHost _host;
    private readonly ILogger<PowerShellGraphUserLookup> _logger;
    private readonly ConcurrentDictionary<string, UserSignInStatus> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public PowerShellGraphUserLookup(
        IPowerShellHost host,
        ILogger<PowerShellGraphUserLookup> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UserSignInStatus> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return UserSignInStatus.LookupFailed(userId ?? string.Empty);

        var key = userId.Trim().ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        UserSignInStatus result;
        try
        {
            var output = await _host.InvokeAsync(
                "Get-MgUser -UserId $UserId -Property 'displayName,userPrincipalName,accountEnabled' -ErrorAction Stop",
                new Dictionary<string, object?> { ["UserId"] = userId },
                streams: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (output.Count == 0)
            {
                result = UserSignInStatus.LookupFailed(userId);
            }
            else
            {
                var u = output[0];
                var displayName = u.Properties["DisplayName"]?.Value?.ToString();
                var upn = u.Properties["UserPrincipalName"]?.Value?.ToString();
                var enabled = u.Properties["AccountEnabled"]?.Value as bool?;

                result = new UserSignInStatus(
                    UserLookupStatus.Ok,
                    DisplayName: displayName,
                    UserPrincipalName: upn,
                    AccountEnabled: enabled);
            }
        }
        catch (PowerShellInvocationException ex)
        {
            // The script's Get-UserSignInStatus catches any failure and returns LOOKUP_FAILED;
            // we do the same so a single missing user doesn't abort the whole audit.
            _logger.LogDebug(ex, "Get-MgUser lookup failed for {UserId}", userId);
            result = UserSignInStatus.LookupFailed(userId);
        }

        _cache[key] = result;
        return result;
    }

    public void Clear() => _cache.Clear();
}
