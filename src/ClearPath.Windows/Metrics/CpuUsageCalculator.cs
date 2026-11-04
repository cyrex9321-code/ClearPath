using ClearPath.Core.Models;

namespace ClearPath.Windows.Metrics;

/// <summary>
/// Pure CPU-utilisation math over two GetSystemTimes samples.
/// GetSystemTimes kernel time <b>includes</b> idle time, so total = Δkernel + Δuser and busy = total − Δidle.
/// </summary>
public static class CpuUsageCalculator
{
    public static Reading<double> Compute(CpuTimes previous, CpuTimes current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User)
        {
            return Reading<double>.Unavailable("Counter went backwards between samples.");
        }

        var idle = current.Idle - previous.Idle;
        var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        if (total == 0)
        {
            return Reading<double>.Unavailable("Sampling interval too short.");
        }

        if (idle > total)
        {
            return Reading<double>.Unavailable("Inconsistent counters (idle exceeds total).");
        }

        var busy = (double)(total - idle) / total * 100.0;
        return Reading<double>.Of(Math.Clamp(busy, 0.0, 100.0));
    }
}
