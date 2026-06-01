using SharedMailbox.Core.Domain;

namespace SharedMailbox.Core.Configuration;

/// <summary>
/// Root configuration object. Bound from appsettings.json (+ optional per-user override)
/// by the App project's IConfigProvider. Everything here is read-only at runtime.
/// </summary>
public sealed class AppConfig
{
    public AzureAdConfig AzureAd { get; init; } = new();
    public IReadOnlyList<SharedMailGroupConfig> KnownGroups { get; init; } = Array.Empty<SharedMailGroupConfig>();

    /// <summary>
    /// Optional role-to-scope mapping for tool-side UX filtering (Layer 2 of the
    /// dual-layer security model). When empty, no filtering is applied and every entry
    /// in <see cref="KnownGroups"/> is shown to the signed-in user regardless of their
    /// memberships. When populated, the sidebar shows only the SharedMail- groups the
    /// user has role-based access to via <see cref="RoleConfig.EntraGroupId"/>.
    /// </summary>
    public IReadOnlyList<RoleConfig> Roles { get; init; } = Array.Empty<RoleConfig>();

    public LoggingConfig Logging { get; init; } = new();
}

/// <summary>
/// Azure AD / Entra app registration settings for MSAL.
/// TenantId and ClientId are required; we crash fast on startup if either is missing.
/// </summary>
public sealed class AzureAdConfig
{
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Graph scopes requested at sign-in. Matches the PowerShell:
    /// Connect-MgGraph -Scopes "Group.Read.All", "User.Read.All"
    /// </summary>
    public IReadOnlyList<string> GraphScopes { get; init; } = new[]
    {
        "Group.Read.All",
        "User.Read.All",
    };

    /// <summary>
    /// Exchange Online OAuth resource. Used by Connect-ExchangeOnline when we pass
    /// a delegated token from MSAL (UserPrincipalName + AccessToken).
    /// </summary>
    public string ExchangeResource { get; init; } = "https://outlook.office365.com";

    /// <summary>
    /// MSAL redirect URI for the public client. For Windows desktop with WAM, the
    /// standard value is "ms-appx-web://microsoft.aad.brokerplugin/{client-id}"; for
    /// embedded WebView2 use http://localhost.
    /// </summary>
    public string RedirectUri { get; init; } = "http://localhost";
}

/// <summary>
/// One row of the known-groups list shown in the UI's group picker. Maps directly to
/// the $groupOptions hashtable entries in the original PowerShell script. Users can
/// always enter a Group Object ID manually as a fallback (mirrors PS "Enter manually").
/// </summary>
public sealed class SharedMailGroupConfig
{
    public string Name { get; init; } = string.Empty;
    public Guid GroupId { get; init; }

    public SharedMailGroup ToDomain() => new(GroupId, Name);
}

/// <summary>
/// File-logging output settings. The Logs/ subfolder is created on demand, matching
/// the script:LogDir bootstrap at the top of the PowerShell.
/// </summary>
public sealed class LoggingConfig
{
    /// <summary>
    /// Directory (absolute, or relative to the executable) where CSV audit/cleanup/bulk-add
    /// logs are written and where Serilog rolls daily app logs.
    /// </summary>
    public string LogDirectory { get; init; } = "Logs";
}
