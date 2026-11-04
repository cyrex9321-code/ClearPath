using ClearPath.Core.Models;

namespace ClearPath.Core.Abstractions;

/// <summary>Read-only system metrics. Implementations must never fabricate values.</summary>
public interface ISystemMetrics
{
    /// <summary>
    /// CPU utilisation (0–100) over the interval since the previous call.
    /// The first call establishes a baseline and returns Unavailable("Measuring…").
    /// </summary>
    Reading<double> SampleCpuPercent();

    Reading<MemoryStatus> GetMemoryStatus();
}

public interface IDriveInfoProvider
{
    /// <summary>Fixed, ready drives only.</summary>
    Reading<IReadOnlyList<DriveStatus>> GetFixedDrives();
}

public interface IOsInfoProvider
{
    Reading<OsInfo> GetOsInfo();
}

public interface IRecycleBinInfo
{
    /// <summary>Size and item count across all drives (SHQueryRecycleBin). Read-only; ClearPath never empties it in the MVP.</summary>
    Reading<RecycleBinStatus> Query();
}

public interface IPrivilegeInfo
{
    Reading<PrivilegeStatus> GetCurrent();
}

/// <summary>Well-known ClearPath locations under %LOCALAPPDATA%\ClearPath.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }
    string LogDirectory { get; }
    string DatabasePath { get; }
    string SettingsPath { get; }
    string DefaultQuarantineDirectory { get; }
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Read access to scan and operation history. Writes belong to the (M3) journal.</summary>
public interface IHistoryRepository
{
    Task<ScanSummary?> GetLastScanAsync(CancellationToken cancellationToken = default);
    Task<OperationSummary?> GetLastCleanupAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationSummary>> GetOperationsAsync(int skip, int take, CancellationToken cancellationToken = default);
    Task<SafetySnapshot> GetSafetySnapshotAsync(int retentionDays, CancellationToken cancellationToken = default);
}

/// <summary>Creates/migrates the journal database schema. Idempotent.</summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates ClearPath-owned directories race-resistantly: walks from the volume root using handle-relative
/// opens, rejects any reparse point on the way, creates missing components with create-new semantics and
/// verifies the final handle path. Never falls back to path-based creation.
/// </summary>
public interface IDirectoryProvisioner
{
    /// <summary>Ensures the directory exists. Throws <see cref="UnsafeDirectoryException"/> when the chain is unsafe.</summary>
    void EnsureDirectory(LocalPath directory);
}

public sealed class UnsafeDirectoryException(string message, Exception? inner = null) : IOException(message, inner);
