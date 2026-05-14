using System.IO;
using Serilog;
using Serilog.Events;
using SharedMailbox.App.Configuration;
using SharedMailbox.Core.Configuration;

namespace SharedMailbox.App.Logging;

/// <summary>
/// Builds the Serilog logger the rest of the app uses for structured logging.
///
/// Sinks:
///   * <b>Rolling file</b> at <c>{LogDirectory}\app-{Date}.log</c>, retained for 14 days.
///     Plain text format with timestamp, level, source context, and message.
///   * <b>Debug-output</b> sink (visible in Visual Studio's Output window) — DEBUG builds only.
///
/// The CSV audit logs the user cares about (mailbox-audit-*.csv, mailbox-cleanup-*.csv,
/// SharedMail-BulkAction-*.csv) are written separately by
/// <see cref="Core.Services.CsvAuditLogWriter"/> into the same directory, so all per-run
/// artifacts live in one place for support / SIEM ingest.
/// </summary>
public static class LoggingBootstrap
{
    public static ILogger Build(LoggingConfig logging, ConfigPaths paths)
    {
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(paths);

        paths.EnsureUserDataDirectory();
        Directory.CreateDirectory(logging.LogDirectory);

        var logFilePath = Path.Combine(logging.LogDirectory, "app-.log");

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}");

#if DEBUG
        loggerConfig = loggerConfig.WriteTo.Debug(
            outputTemplate: "[{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}");
#endif

        return loggerConfig.CreateLogger();
    }
}
