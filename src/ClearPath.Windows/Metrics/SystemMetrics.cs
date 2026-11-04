using System.Diagnostics;
using System.Runtime.InteropServices;
using ClearPath.Core.Abstractions;
using ClearPath.Core.Models;
using ClearPath.Windows.Native;
using Microsoft.Extensions.Logging;

namespace ClearPath.Windows.Metrics;

/// <summary>CPU via GetSystemTimes (primary, per plan), RAM via GlobalMemoryStatusEx.</summary>
public sealed class SystemMetrics(ILogger<SystemMetrics> logger) : ISystemMetrics
{
    /// <summary>Samples closer together than this give a noisy figure; keep the baseline and report "Measuring…".</summary>
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private CpuTimes? _previous;
    private long _previousTimestamp;

    public Reading<double> SampleCpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var error = Marshal.GetLastPInvokeError();
            logger.LogWarning("GetSystemTimes failed with Win32 error {Error}", error);
            return Reading<double>.Unavailable($"GetSystemTimes failed (error {error}).");
        }

        var now = new CpuTimes((ulong)idle, (ulong)kernel, (ulong)user);
        var timestamp = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (_previous is not { } previous)
            {
                _previous = now;
                _previousTimestamp = timestamp;
                return Reading<double>.Unavailable("Measuring…");
            }

            if (Stopwatch.GetElapsedTime(_previousTimestamp, timestamp) < MinimumInterval)
            {
                return Reading<double>.Unavailable("Measuring…");
            }

            _previous = now;
            _previousTimestamp = timestamp;
            return CpuUsageCalculator.Compute(previous, now);
        }
    }

    public Reading<MemoryStatus> GetMemoryStatus()
    {
        var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref status))
        {
            var error = Marshal.GetLastPInvokeError();
            logger.LogWarning("GlobalMemoryStatusEx failed with Win32 error {Error}", error);
            return Reading<MemoryStatus>.Unavailable($"GlobalMemoryStatusEx failed (error {error}).");
        }

        return Reading<MemoryStatus>.Of(new MemoryStatus(status.ullTotalPhys, status.ullAvailPhys, status.dwMemoryLoad));
    }
}
