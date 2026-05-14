using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SharedMailbox.Core.Configuration;

namespace SharedMailbox.App.Configuration;

/// <summary>
/// Default <see cref="IConfigProvider"/>. Layers three optional JSON sources, last-wins:
///   1. Bundled <c>appsettings.json</c> next to the executable          (required)
///   2. <c>appsettings.local.json</c> next to the executable             (dev override, gitignored)
///   3. <c>%LOCALAPPDATA%\entra-shared-mailbox-manager\appsettings.json</c> (per-user override)
///
/// After loading, the resolved <see cref="AppConfig.Logging"/>'s LogDirectory is rewritten
/// to an absolute path under the user-data directory if it was supplied as relative,
/// because the install directory may be read-only (MSIX).
///
/// Post-load validation rejects placeholder Tenant/Client IDs and unparseable group GUIDs
/// with a single <see cref="ConfigurationException"/> listing every problem found.
/// </summary>
public sealed class JsonConfigProvider : IConfigProvider
{
    private static readonly Guid PlaceholderGuid = Guid.Empty;

    private readonly ConfigPaths _paths;
    private readonly ILogger<JsonConfigProvider> _logger;

    public JsonConfigProvider(ConfigPaths paths, ILogger<JsonConfigProvider> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.BundledConfigPath))
        {
            throw new ConfigurationException(
                $"appsettings.json not found at {_paths.BundledConfigPath}. " +
                "The app cannot start without a bundled default configuration. " +
                "If you're running from a build output, ensure appsettings.json is set to " +
                "'Copy to output directory: Copy if newer' in the project.");
        }

        _logger.LogInformation("Loading configuration from {Bundled}", _paths.BundledConfigPath);

        var builder = new ConfigurationBuilder()
            .AddJsonFile(_paths.BundledConfigPath, optional: false, reloadOnChange: false);

        if (File.Exists(_paths.DevLocalConfigPath))
        {
            _logger.LogInformation("Layering dev-local override {Path}", _paths.DevLocalConfigPath);
            builder.AddJsonFile(_paths.DevLocalConfigPath, optional: true, reloadOnChange: false);
        }

        if (File.Exists(_paths.UserConfigPath))
        {
            _logger.LogInformation("Layering per-user override {Path}", _paths.UserConfigPath);
            builder.AddJsonFile(_paths.UserConfigPath, optional: true, reloadOnChange: false);
        }

        var raw = builder.Build();
        var config = raw.Get<AppConfig>()
            ?? throw new ConfigurationException(
                "Configuration loaded as null. Check that appsettings.json is valid JSON.");

        config = ResolveLogDirectory(config);
        Validate(config);

        _logger.LogInformation(
            "Configuration ready: tenant {Tenant}, {GroupCount} known group(s), logs at {Logs}",
            config.AzureAd.TenantId, config.KnownGroups.Count, config.Logging.LogDirectory);

        return Task.FromResult(config);
    }

    // -----------------------------------------------------------------------
    // Internals
    // -----------------------------------------------------------------------

    private AppConfig ResolveLogDirectory(AppConfig config)
    {
        var logDir = config.Logging.LogDirectory;
        if (string.IsNullOrWhiteSpace(logDir)) logDir = "Logs";

        if (!Path.IsPathRooted(logDir))
        {
            // Relative paths anchor at %LOCALAPPDATA% — never at AppContext.BaseDirectory,
            // because MSIX-installed apps can't write into their install location.
            logDir = Path.Combine(_paths.UserDataDirectory, logDir);
        }

        return new AppConfig
        {
            AzureAd = config.AzureAd,
            KnownGroups = config.KnownGroups,
            Logging = new LoggingConfig { LogDirectory = logDir },
        };
    }

    private static void Validate(AppConfig config)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(config.AzureAd.TenantId))
        {
            problems.Add("AzureAd.TenantId is missing.");
        }
        else if (!Guid.TryParse(config.AzureAd.TenantId, out var tid) || tid == PlaceholderGuid)
        {
            problems.Add($"AzureAd.TenantId is not a valid Guid (got '{config.AzureAd.TenantId}'). " +
                         "Replace the placeholder with your tenant ID.");
        }

        if (string.IsNullOrWhiteSpace(config.AzureAd.ClientId))
        {
            problems.Add("AzureAd.ClientId is missing.");
        }
        else if (!Guid.TryParse(config.AzureAd.ClientId, out var cid) || cid == PlaceholderGuid)
        {
            problems.Add($"AzureAd.ClientId is not a valid Guid (got '{config.AzureAd.ClientId}'). " +
                         "Replace the placeholder with your Entra app registration's Application (client) ID.");
        }

        if (config.AzureAd.GraphScopes is null || config.AzureAd.GraphScopes.Count == 0)
        {
            problems.Add("AzureAd.GraphScopes must contain at least one scope (e.g., 'Group.Read.All').");
        }

        for (var i = 0; i < config.KnownGroups.Count; i++)
        {
            var g = config.KnownGroups[i];
            if (string.IsNullOrWhiteSpace(g.Name))
                problems.Add($"KnownGroups[{i}].Name is missing.");
            if (g.GroupId == PlaceholderGuid)
                problems.Add($"KnownGroups[{i}].GroupId is empty or all zeros (name='{g.Name}').");
        }

        if (problems.Count > 0)
        {
            throw new ConfigurationException(
                "Configuration validation failed:\n  - " + string.Join("\n  - ", problems) +
                "\n\nEdit appsettings.json (or appsettings.local.json) and restart the app.");
        }
    }
}
