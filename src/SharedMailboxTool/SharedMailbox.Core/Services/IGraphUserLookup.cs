using SharedMailbox.Core.Domain;

namespace SharedMailbox.Core.Services;

/// <summary>
/// Retrieves a user's display name, UPN and AccountEnabled flag from Microsoft Graph
/// with in-memory caching keyed by normalized identity (lower-cased, trimmed).
///
/// Mirrors the $script:GraphUserCache + Get-UserSignInStatus pair in the original
/// PowerShell. The cache exists because audits across "ALL mailboxes in a group"
/// typically resolve the same handful of admins/sysadmins dozens of times.
///
/// Implementations should never throw for a missing user; they should return
/// <see cref="UserSignInStatus"/> with LookupStatus = LookupFailed instead.
/// </summary>
public interface IGraphUserLookup
{
    Task<UserSignInStatus> GetAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Drop the in-memory cache. Called on sign-out and between sessions.</summary>
    void Clear();
}
