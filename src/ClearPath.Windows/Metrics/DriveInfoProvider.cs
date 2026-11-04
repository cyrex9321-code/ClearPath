using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using Microsoft.Extensions.Logging;

namespace ClearPath.Windows.Metrics;

public sealed class DriveInfoProvider(ILogger<DriveInfoProvider> logger) : IDriveInfoProvider
{
    public Reading<IReadOnlyList<DriveStatus>> GetFixedDrives()
    {
        try
        {
            var result = new List<DriveStatus>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                try
                {
                    if (!drive.IsReady) continue;
                    result.Add(new DriveStatus(
                        drive.RootDirectory.FullName,
                        drive.VolumeLabel,
                        drive.DriveFormat,
                        DriveKind.Fixed,
                        (ulong)drive.TotalSize,
                        (ulong)drive.TotalFreeSpace));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Drive {Drive} could not be queried", drive.Name);
                }
            }

            return Reading<IReadOnlyList<DriveStatus>>.Of(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Drive enumeration failed");
            return Reading<IReadOnlyList<DriveStatus>>.Unavailable("Drive enumeration failed.");
        }
    }
}
