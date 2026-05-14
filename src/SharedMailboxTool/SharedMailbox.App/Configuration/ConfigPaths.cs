using System.IO;

namespace SharedMailbox.App.Configuration;

/// <summary>
/// Centralizes the writable / read-only path choices the app makes at startup.
///
/// Rationale: an MSIX-packaged app's install directory is read-only, so logs,
/// per-user config overrides, and MSAL token caches must all live somewhere
/// the user has write access to. We anchor those at
/// <c>%LOCALAPPDATA%\entra-shared-mailbox-manager\</c>.
///
/// The bundled (read-only) <c>appsettings.json</c> lives next to the executable.
/// Per-user overrides live in <see cref="UserDataDirectory"/>. The Config Builder
/// companion app (future) and Intune device scripts both write to that same path.
/// </summary>
public sealed class ConfigPaths
{
    /// <summary>The "company\product" folder name used under LocalAppData / ProgramData.</summary>
    public const string ProductFolderName = "entra-shared-mailbox-manager";

    /// <summary>
    /// Where the executable and its bundled <c>appsettings.json</c> live. Read-only when
    /// the app is installed as MSIX.
    /// </summary>
    public string AppBaseDirectory { get; }

    /// <summary>
    /// Per-user writable directory. Holds optional <c>appsettings.json</c> overrides,
    /// the CSV log directory, Serilog rolling files, and the MSAL token cache.
    /// Created on first access.
    /// </summary>
    public string UserDataDirectory { get; }

    /// <summary>Default <c>Logs/</c> directory under <see cref="UserDataDirectory"/>.</summary>
    public string DefaultLogDirectory => Path.Combine(UserDataDirectory, "Logs");

    /// <summary>Bundled (deployed-default) <c>appsettings.json</c> path.</summary>
    public string BundledConfigPath => Path.Combine(AppBaseDirectory, "appsettings.json");

    /// <summary>Developer-local override next to the bundled config — git-ignored.</summary>
    public string DevLocalConfigPath => Path.Combine(AppBaseDirectory, "appsettings.local.json");

    /// <summary>Per-user override in <see cref="UserDataDirectory"/> — never committed.</summary>
    public string UserConfigPath => Path.Combine(UserDataDirectory, "appsettings.json");

    public ConfigPaths()
        : this(AppContext.BaseDirectory, ResolveLocalAppData())
    {
    }

    // Test-friendly constructor: lets fixtures point at a temp directory.
    public ConfigPaths(string appBaseDirectory, string userDataDirectory)
    {
        AppBaseDirectory = appBaseDirectory ?? throw new ArgumentNullException(nameof(appBaseDirectory));
        UserDataDirectory = userDataDirectory ?? throw new ArgumentNullException(nameof(userDataDirectory));
    }

    /// <summary>
    /// Ensure <see cref="UserDataDirectory"/> exists. Cheap to call repeatedly;
    /// <see cref="Directory.CreateDirectory(string)"/> is a no-op when the directory exists.
    /// </summary>
    public void EnsureUserDataDirectory()
    {
        Directory.CreateDirectory(UserDataDirectory);
    }

    private static string ResolveLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, ProductFolderName);
    }
}
