using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.File;

namespace DvfApi.Logging;

/// <summary>
/// Builds the Serilog logger used by the application: console output (mirroring the configured
/// Microsoft logging levels) plus a file sink rolling daily into the folder configured in
/// appsettings.json ("Configuration:LogsFolder").
///
/// The file template includes {CorrelationId}: RequestLoggingMiddleware pushes the request id
/// (X-Request-Id header value, or a generated GUID) onto the log context for every request,
/// so every line logged while a request is being processed carries it. Events logged outside
/// a request (startup, import) get a "-" instead of "null".
/// </summary>
public static class SerilogConfiguration
{
    /// <summary>Default log file folder, relative to the application base directory.</summary>
    private const string DefaultLogPath = "logs";

    private const string ConsoleTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] {Level:u3} {CorrelationId} {Message:lj}{NewLine}{Exception}";

    private const string FileTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] {Level:u3} {CorrelationId} {Message:lj}{NewLine}{Exception}";

    public static Logger Create(IConfiguration configuration, IHostEnvironment environment)
    {
        // Resolve the log folder from configuration; an empty or missing value falls back to
        // a "logs" folder next to the binary (the folder is created if it does not exist).
        var logPath = configuration["Configuration:LogsFolder"];
        if (string.IsNullOrWhiteSpace(logPath))
        {
            logPath = DefaultLogPath;
        }

        logPath = Path.IsPathRooted(logPath) ? logPath : Path.GetFullPath(Path.Combine(environment.ContentRootPath, logPath));
        Directory.CreateDirectory(logPath);

        // Mirror the Microsoft logging level settings ("Logging:LogLevel:Default" and
        // "Logging:LogLevel:Microsoft.AspNetCore") so Serilog respects the existing configuration.
        var defaultLevel = ParseLevel(configuration["Logging:LogLevel:Default"], LogEventLevel.Information);
        var aspnetCoreLevel = ParseLevel(configuration["Logging:LogLevel:Microsoft.AspNetCore"], LogEventLevel.Warning);

        return new LoggerConfiguration()
            .MinimumLevel.Is(defaultLevel)
            .MinimumLevel.Override("Microsoft.AspNetCore", aspnetCoreLevel)
            // Read properties pushed on Serilog's AsyncLocal log context (e.g. the correlation
            // id set by RequestLoggingMiddleware for the duration of each request).
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            // Default value for the correlation id so events logged outside a request (startup,
            // import) get a stable value instead of the "null" rendered for a missing property;
            // request events carry the real request id (set by RequestLoggingMiddleware).
            .Enrich.With(new RequestIdFallbackEnricher())
            .WriteTo.Console(outputTemplate: ConsoleTemplate)
            .WriteTo.File(
                path: Path.Combine(logPath, "log-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                outputTemplate: FileTemplate,
                shared: true)
            .CreateLogger();
    }

    /// <summary>Parses a log level string ("Information", "Warning", ...); unknown values fall back to the default.</summary>
    private static LogEventLevel ParseLevel(string? value, LogEventLevel fallback) =>
        value is not null && Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out var level)
            ? level
            : fallback;
}

/// <summary>
/// Adds a "CorrelationId" property to every event when it is not already present (request
/// events get the real request id from <see cref="RequestLoggingMiddleware"/>).
/// </summary>
public sealed class RequestIdFallbackEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue("CorrelationId", out _))
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("CorrelationId", "-"));
    }
}
