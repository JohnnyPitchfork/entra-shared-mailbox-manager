using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SharedMailbox.App.Authentication;
using SharedMailbox.App.Configuration;
using SharedMailbox.App.Logging;
using SharedMailbox.App.Services;
using SharedMailbox.App.ViewModels;
using SharedMailbox.Core.Configuration;
using SharedMailbox.Core.Services;
using SharedMailbox.PowerShell.Adapters;
using SharedMailbox.PowerShell.Hosting;

namespace SharedMailbox.App;

/// <summary>
/// Application entry point and DI composition root.
///
/// Startup order:
///   1. Build <see cref="ConfigPaths"/> (no dependencies).
///   2. Load and validate <see cref="AppConfig"/> via <see cref="JsonConfigProvider"/>.
///      Any validation failure surfaces in a startup MessageBox and exits the process.
///   3. Build the Serilog logger from <c>AppConfig.Logging</c>.
///   4. Build the Generic Host, registering everything as singletons (the runspace,
///      the MSAL client, and the EXO connection are all per-process resources).
///   5. Resolve <see cref="MainWindow"/> and Show().
///
/// Shutdown calls <see cref="IHost.StopAsync"/> which disposes
/// <see cref="IPowerShellHost"/> (closes the runspace) and
/// <see cref="MsalAccessTokenProvider"/> (unregisters the cache helper).
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>The application's composition root. Throws if accessed before OnStartup completes.</summary>
    public IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException("Host is not yet built.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _host = BuildHost();
            await _host.StartAsync().ConfigureAwait(true);

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (ConfigurationException ex)
        {
            // Validation problem (placeholder GUIDs, missing fields, etc.). The exception
            // message is intentionally formatted as a multi-line user-readable list.
            MessageBox.Show(
                ex.Message,
                "Configuration error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(exitCode: 2);
        }
        catch (Exception ex)
        {
            // Last-chance handler. Anything else (missing PS modules, unexpected I/O, etc.)
            // lands here. Show the message + first line of stack so the user has something
            // actionable to send IT or paste into a bug report.
            MessageBox.Show(
                $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace?.Split('\n').FirstOrDefault()}",
                "Startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(exitCode: 1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            }
            catch
            {
                // Swallow shutdown errors — we're exiting anyway.
            }

            _host.Dispose();
            _host = null;
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    // -----------------------------------------------------------------------
    // Host construction
    // -----------------------------------------------------------------------

    private static IHost BuildHost()
    {
        // Step 1: paths (LOCALAPPDATA resolution, no deps).
        var paths = new ConfigPaths();

        // Step 2: bootstrap logger for the config-load step. We can't use the real
        // Serilog logger yet because its sink directory depends on AppConfig.Logging.
        using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddDebug());
        var bootstrapLogger = bootstrapLoggerFactory.CreateLogger<JsonConfigProvider>();

        // Step 3: load + validate config. Synchronous-over-async is safe here because
        // JsonConfigProvider.LoadAsync is itself synchronous (returns Task.FromResult).
        var configProvider = new JsonConfigProvider(paths, bootstrapLogger);
        var appConfig = configProvider.LoadAsync().GetAwaiter().GetResult();

        // Step 4: build the real Serilog logger now that we know the log directory.
        var serilog = LoggingBootstrap.Build(appConfig.Logging, paths);
        Log.Logger = serilog;

        // Step 5: build the host with everything registered as singletons.
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(serilog, dispose: true);

        ConfigureServices(builder.Services, paths, appConfig);
        return builder.Build();
    }

    private static void ConfigureServices(IServiceCollection services, ConfigPaths paths, AppConfig appConfig)
    {
        // Pre-built singletons we already have.
        services.AddSingleton(paths);
        services.AddSingleton(appConfig);
        services.AddSingleton(appConfig.AzureAd);
        services.AddSingleton(appConfig.Logging);

        // Config provider (kept registered for future reload-on-demand scenarios).
        services.AddSingleton<IConfigProvider, JsonConfigProvider>();

        // Authentication.
        services.AddSingleton<IAccessTokenProvider, MsalAccessTokenProvider>();

        // PowerShell host + adapters. All singletons:
        //   * The runspace and its module-import cost are amortized across the process.
        //   * EXO + Graph connections are per-process; you don't want two simultaneous.
        //   * Graph user-lookup cache is shared across audits.
        services.AddSingleton<IPowerShellHost, PowerShellHost>();
        services.AddSingleton<IConnectionService, PowerShellConnectionService>();
        services.AddSingleton<IGraphUserLookup, PowerShellGraphUserLookup>();
        services.AddSingleton<ISharedMailboxService, PowerShellSharedMailboxService>();
        services.AddSingleton<IUserGroupMembershipProvider, PowerShellUserGroupMembershipProvider>();

        // Authorization service (pure logic, lives in Core, consumes IUserGroupMembershipProvider).
        services.AddSingleton<IUserAuthorizationService, DefaultUserAuthorizationService>();

        // Core utilities.
        services.AddSingleton<IAuditLogWriter, CsvAuditLogWriter>();
        services.AddSingleton<IUpnImportReader, UpnImportReader>();

        // App-layer services. The confirmation services own their dialog windows'
        // lifecycle and are consumed by the destructive flow VMs; this keeps the
        // VMs free of UI types and makes them unit-testable with a fake.
        services.AddSingleton<ICleanupConfirmationService, CleanupConfirmationService>();
        services.AddSingleton<IBulkGrantConfirmationService, BulkGrantConfirmationService>();

        // View models. Singletons so navigating away from a tab doesn't lose state
        // and so the group picker is shared across every flow.
        services.AddSingleton<GroupPickerViewModel>();
        services.AddSingleton<AuditViewModel>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<BulkGrantViewModel>();
        services.AddSingleton<MainViewModel>();

        // The main window. Singleton because there is exactly one for the app's lifetime.
        services.AddSingleton<MainWindow>();
    }
}
