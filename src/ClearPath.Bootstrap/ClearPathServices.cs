using ClearPath.Backup;
using ClearPath.Cleaning;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Cleaning;
using ClearPath.Core.FileSystem;
using ClearPath.Core.Models;
using ClearPath.Infrastructure;
using ClearPath.Infrastructure.Database;
using ClearPath.Infrastructure.Logging;
using ClearPath.Scanning;
using ClearPath.Windows.FileSystem;
using ClearPath.Windows.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace ClearPath.Bootstrap;

/// <summary>
/// Composition root. The only place that knows concrete implementation types; the App references Core + Bootstrap only.
/// </summary>
public static class ClearPathServices
{
    /// <summary>
    /// Provisions ClearPath's own directories (race-resistant, handle-based), configures logging and registers all services.
    /// </summary>
    public static IHostApplicationBuilder AddClearPath(this IHostApplicationBuilder builder, string? dataDirectoryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var paths = new AppPaths(dataDirectoryOverride);
        ProvisionDirectories(paths);

        Log.Logger = LoggingSetup.CreateLogger(paths);
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger, dispose: true);

        builder.Services.AddClearPathCore(paths);
        return builder;
    }

    /// <summary>Registers services without host/logging configuration (used by tests).</summary>
    public static IServiceCollection AddClearPathCore(this IServiceCollection services, IAppPaths paths)
    {
        services.AddSingleton(paths);

        // Windows integration (read-only providers + handle-based provisioning primitive)
        services.AddSingleton<ISystemMetrics, SystemMetrics>();
        services.AddSingleton<IDriveInfoProvider, DriveInfoProvider>();
        services.AddSingleton<IOsInfoProvider, OsInfoProvider>();
        services.AddSingleton<IRecycleBinInfo, RecycleBinInfo>();
        services.AddSingleton<IPrivilegeInfo, PrivilegeInfo>();
        services.AddSingleton<IDirectoryProvisioner, HandleDirectoryProvisioner>();

        // Infrastructure
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<IHistoryRepository, SqliteHistoryRepository>();
        services.AddSingleton<SqliteScanStore>();
        services.AddSingleton<IScanStore>(sp => sp.GetRequiredService<SqliteScanStore>());
        services.AddSingleton<IScanWriter>(sp => sp.GetRequiredService<SqliteScanStore>());
        services.AddSingleton<IOperationJournal, SqliteOperationJournal>();

        // M3: native handle-based filesystem access (docs/15 §3, §5)
        services.AddSingleton<CapabilitySettings>();
        services.AddSingleton<ICapabilitySettings>(sp => sp.GetRequiredService<CapabilitySettings>());
        services.AddSingleton<IFileSystemReader, FileSystemReader>();
        services.AddSingleton<IFileSystemWriter, FileSystemWriter>();
        services.AddSingleton<ICapabilityProbe, CapabilityProbe>();
        services.AddSingleton<ICapabilityGate>(sp => sp.GetRequiredService<CapabilitySettings>());

        // Scanning (read-only)
        services.AddSingleton<IRuleCatalog, RuleCatalog>();
        services.AddSingleton<IScanner, Scanner>();

        // Cleaning (sole authority over user source files) and Backup (quarantine storage only)
        services.AddSingleton<IPathGuard, PathGuard>();
        services.AddSingleton<IQuarantineStore, QuarantineStore>();
        services.AddSingleton<ICleaner, Cleaner>();
        services.AddSingleton<IRecoveryService, RecoveryService>();

        return services;
    }

    private static void ProvisionDirectories(IAppPaths paths)
    {
        var provisioner = new HandleDirectoryProvisioner(NullLogger<HandleDirectoryProvisioner>.Instance);
        provisioner.EnsureDirectory(LocalPath.Parse(paths.DataDirectory));
        provisioner.EnsureDirectory(LocalPath.Parse(paths.LogDirectory));
    }
}
