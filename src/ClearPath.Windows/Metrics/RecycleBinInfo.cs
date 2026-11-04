using System.Runtime.InteropServices;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using ClearPath.Windows.Native;
using Microsoft.Extensions.Logging;

namespace ClearPath.Windows.Metrics;

/// <summary>Query-only. ClearPath does not empty the Recycle Bin in the MVP.</summary>
public sealed class RecycleBinInfo(ILogger<RecycleBinInfo> logger) : IRecycleBinInfo
{
    public Reading<RecycleBinStatus> Query()
    {
        var info = new NativeMethods.SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
        var hr = NativeMethods.SHQueryRecycleBin(null, ref info);
        if (hr != 0)
        {
            logger.LogWarning("SHQueryRecycleBin failed with HRESULT 0x{Hr:X8}", hr);
            return Reading<RecycleBinStatus>.Unavailable($"SHQueryRecycleBin failed (0x{hr:X8}).");
        }

        return Reading<RecycleBinStatus>.Of(new RecycleBinStatus((ulong)Math.Max(0, info.i64Size), (ulong)Math.Max(0, info.i64NumItems)));
    }
}
