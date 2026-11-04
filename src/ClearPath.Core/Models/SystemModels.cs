namespace ClearPath.Core.Models;

/// <summary>Raw cumulative CPU times (100-ns ticks) as reported by GetSystemTimes. Kernel time includes idle time.</summary>
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

public sealed record MemoryStatus(ulong TotalPhysicalBytes, ulong AvailablePhysicalBytes, uint MemoryLoadPercent)
{
    public ulong UsedPhysicalBytes => TotalPhysicalBytes - AvailablePhysicalBytes;
}

public enum DriveKind { Fixed, Removable, Network, CdRom, Ram, Unknown }

public sealed record DriveStatus(
    string RootPath,
    string VolumeLabel,
    string FileSystem,
    DriveKind Kind,
    ulong TotalBytes,
    ulong FreeBytes)
{
    public ulong UsedBytes => TotalBytes - FreeBytes;
    public double UsedFraction => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;
}

public sealed record OsInfo(
    string ProductName,
    string? DisplayVersion,
    int BuildNumber,
    int UpdateBuildRevision,
    string Architecture,
    string MachineName)
{
    /// <summary>Windows 11 reports ProductName "Windows 10 ..." in the registry; the build number is authoritative.</summary>
    public bool IsWindows11 => BuildNumber >= 22000;
}

public sealed record RecycleBinStatus(ulong TotalBytes, ulong ItemCount);

public sealed record PrivilegeStatus(bool IsElevated, bool IsAdministratorsMember, string UserName, string IntegrityLevel);
