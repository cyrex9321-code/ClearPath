using ClearPath.Core.Abstractions;
using Serilog;
using Serilog.Events;

namespace ClearPath.Infrastructure.Logging;

public static class LoggingSetup
{
    /// <summary>Rolling daily log files in %LOCALAPPDATA%\ClearPath\logs, 14 files retained.</summary>
    public static Serilog.ILogger CreateLogger(IAppPaths paths) =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "clearpath-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
}
