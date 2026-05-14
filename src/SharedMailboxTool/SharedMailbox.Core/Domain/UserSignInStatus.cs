namespace SharedMailbox.Core.Domain;

/// <summary>
/// Result of looking up an account's sign-in state via Microsoft Graph.
/// Mirrors the columns returned by Get-UserSignInStatus in the original script:
/// LookupStatus, DisplayName, UserPrincipalName, AccountEnabled.
/// </summary>
public sealed record UserSignInStatus(
    UserLookupStatus LookupStatus,
    string? DisplayName,
    string? UserPrincipalName,
    bool? AccountEnabled)
{
    /// <summary>
    /// True when we successfully looked up the user AND they are disabled in Entra.
    /// Null when the lookup failed (we shouldn't assume blocked-vs-not on a failed lookup).
    /// </summary>
    public bool? SignInBlocked =>
        LookupStatus == UserLookupStatus.Ok && AccountEnabled is { } enabled
            ? !enabled
            : null;

    public static UserSignInStatus LookupFailed(string identity) =>
        new(UserLookupStatus.LookupFailed, DisplayName: null, UserPrincipalName: identity, AccountEnabled: null);
}

public enum UserLookupStatus
{
    Ok,
    LookupFailed,
}
