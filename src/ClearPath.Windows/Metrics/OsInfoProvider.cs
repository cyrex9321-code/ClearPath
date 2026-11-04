using System.Runtime.InteropServices;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ClearPath.Windows.Metrics;

public sealed class OsInfoProvider(ILogger<OsInfoProvider> logger) : IOsInfoProvider
{
    public Reading<OsInfo> GetOsInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            if (key is null) return Reading<OsInfo>.Unavailable("CurrentVersion key not found.");

            var productName = key.GetValue("ProductName") as string ?? "Windows";
            var displayVersion = key.GetValue("DisplayVersion") as string;
            var build = int.TryParse(key.GetValue("CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
            var ubr = key.GetValue("UBR") is int u ? u : 0;

            // The registry still says "Windows 10" on Windows 11; the build number is authoritative.
            if (build >= 22000 && productName.StartsWith("Windows 10", StringComparison.Ordinal))
            {
                productName = "Windows 11" + productName["Windows 10".Length..];
            }

            return Reading<OsInfo>.Of(new OsInfo(
                productName,
                displayVersion,
                build,
                ubr,
                RuntimeInformation.OSArchitecture.ToString(),
                Environment.MachineName));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "OS information could not be read");
            return Reading<OsInfo>.Unavailable("OS information could not be read.");
        }
    }
}
