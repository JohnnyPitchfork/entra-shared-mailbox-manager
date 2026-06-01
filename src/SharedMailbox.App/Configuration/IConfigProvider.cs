using SharedMailbox.Core.Configuration;

namespace SharedMailbox.App.Configuration;

/// <summary>
/// Loads the <see cref="AppConfig"/> the rest of the application binds against.
///
/// Implementations layer multiple sources (bundled defaults + per-user overrides today;
/// SharePoint-hosted central config in Pattern A later) and produce a single fully-resolved
/// <see cref="AppConfig"/>. The resolved config is validated — implementations throw
/// <see cref="ConfigurationException"/> when a required value is missing or still set to a
/// known placeholder (e.g., a TenantId of all zeros).
/// </summary>
public interface IConfigProvider
{
    Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown when configuration is missing or invalid. The message is intended to be
/// surfaced verbatim in a startup error dialog so the user knows exactly what to fix
/// and where the file lives.
/// </summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }
    public ConfigurationException(string message, Exception inner) : base(message, inner) { }
}
