using System.Security.Principal;
using ClearPath.Core.Models;
using ClearPath.Windows.Metrics;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPath.Windows.Tests;

public class CpuUsageCalculatorTests
{
    // GetSystemTimes: kernel time includes idle time.
    [Fact]
    public void Fifty_percent_busy()
    {
        // total = (100 + 100) = 200 ticks, idle = 100 => 50% busy
        var r = CpuUsageCalculator.Compute(new CpuTimes(0, 0, 0), new CpuTimes(Idle: 100, Kernel: 100, User: 100));
        r.IsAvailable.Should().BeTrue();
        r.Value.Should().BeApproximately(50.0, 1e-9);
    }

    [Fact]
    public void Zero_percent_when_all_kernel_time_is_idle()
    {
        var r = CpuUsageCalculator.Compute(new CpuTimes(10, 10, 5), new CpuTimes(Idle: 110, Kernel: 110, User: 5));
        r.IsAvailable.Should().BeTrue();
        r.Value.Should().BeApproximately(0.0, 1e-9);
    }

    [Fact]
    public void Hundred_percent_when_idle_does_not_advance()
    {
        var r = CpuUsageCalculator.Compute(new CpuTimes(10, 10, 10), new CpuTimes(Idle: 10, Kernel: 60, User: 60));
        r.IsAvailable.Should().BeTrue();
        r.Value.Should().BeApproximately(100.0, 1e-9);
    }

    [Fact]
    public void Uses_baseline_deltas_not_absolute_values()
    {
        // delta idle 25, delta kernel 50, delta user 50 => total 100 => 75% busy
        var r = CpuUsageCalculator.Compute(new CpuTimes(1_000, 5_000, 3_000), new CpuTimes(1_025, 5_050, 3_050));
        r.Value.Should().BeApproximately(75.0, 1e-9);
    }

    [Fact]
    public void Zero_interval_is_unavailable()
    {
        var t = new CpuTimes(5, 10, 10);
        var r = CpuUsageCalculator.Compute(t, t);
        r.IsAvailable.Should().BeFalse();
        r.UnavailableReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Idle_counter_going_backwards_is_unavailable()
        => CpuUsageCalculator.Compute(new CpuTimes(100, 100, 100), new CpuTimes(50, 200, 200)).IsAvailable.Should().BeFalse();

    [Fact]
    public void Kernel_counter_going_backwards_is_unavailable()
        => CpuUsageCalculator.Compute(new CpuTimes(100, 100, 100), new CpuTimes(150, 90, 200)).IsAvailable.Should().BeFalse();

    [Fact]
    public void User_counter_going_backwards_is_unavailable()
        => CpuUsageCalculator.Compute(new CpuTimes(100, 100, 100), new CpuTimes(150, 200, 90)).IsAvailable.Should().BeFalse();

    [Fact]
    public void Idle_greater_than_total_is_unavailable()
    {
        var r = CpuUsageCalculator.Compute(new CpuTimes(0, 0, 0), new CpuTimes(Idle: 200, Kernel: 100, User: 50));
        r.IsAvailable.Should().BeFalse();
        r.UnavailableReason.Should().Contain("idle");
    }

    [Fact]
    public void Result_is_always_within_range_for_random_consistent_inputs()
    {
        var rng = new Random(12345);
        for (var i = 0; i < 2000; i++)
        {
            var dk = (ulong)rng.Next(1, 1_000_000);
            var du = (ulong)rng.Next(0, 1_000_000);
            var di = (ulong)rng.NextInt64(0, (long)(dk + du) + 1);
            var r = CpuUsageCalculator.Compute(new CpuTimes(7, 11, 13), new CpuTimes(7 + di, 11 + dk, 13 + du));
            r.IsAvailable.Should().BeTrue();
            r.Value.Should().BeInRange(0.0, 100.0);
        }
    }
}

public class SystemMetricsTests
{
    private static SystemMetrics Create() => new(NullLogger<SystemMetrics>.Instance);

    [Fact]
    public void First_sample_is_unavailable_measuring()
    {
        var r = Create().SampleCpuPercent();
        r.IsAvailable.Should().BeFalse();
        r.UnavailableReason.Should().Be("Measuring…");
    }

    [Fact]
    public void Sample_too_soon_after_baseline_stays_measuring()
    {
        var m = Create();
        m.SampleCpuPercent();
        var r = m.SampleCpuPercent(); // well under the 250 ms minimum interval
        r.IsAvailable.Should().BeFalse();
        r.UnavailableReason.Should().Be("Measuring…");
    }

    [Fact]
    public async Task Second_sample_after_interval_is_a_percentage()
    {
        var m = Create();
        m.SampleCpuPercent();
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var r = m.SampleCpuPercent();
        r.IsAvailable.Should().BeTrue(r.UnavailableReason);
        r.Value.Should().BeInRange(0.0, 100.0);
    }

    [Fact]
    public void Memory_status_is_consistent()
    {
        var r = Create().GetMemoryStatus();
        r.IsAvailable.Should().BeTrue(r.UnavailableReason);
        var s = r.Value!;
        s.TotalPhysicalBytes.Should().BeGreaterThan(0);
        s.AvailablePhysicalBytes.Should().BeLessThanOrEqualTo(s.TotalPhysicalBytes);
        s.MemoryLoadPercent.Should().BeLessThanOrEqualTo(100);
        s.UsedPhysicalBytes.Should().Be(s.TotalPhysicalBytes - s.AvailablePhysicalBytes);
    }
}

public class DriveInfoProviderTests
{
    [Fact]
    public void Returns_at_least_one_fixed_drive_with_sane_numbers()
    {
        var r = new DriveInfoProvider(NullLogger<DriveInfoProvider>.Instance).GetFixedDrives();
        r.IsAvailable.Should().BeTrue(r.UnavailableReason);
        r.Value.Should().NotBeEmpty();
        foreach (var d in r.Value!)
        {
            d.Kind.Should().Be(DriveKind.Fixed);
            d.RootPath.Should().MatchRegex(@"^[A-Za-z]:\\$");
            d.TotalBytes.Should().BeGreaterThan(0);
            d.FreeBytes.Should().BeLessThanOrEqualTo(d.TotalBytes);
            d.UsedFraction.Should().BeInRange(0.0, 1.0);
        }
    }
}

public class OsInfoProviderTests
{
    [Fact]
    public void Reports_supported_build_and_name()
    {
        var r = new OsInfoProvider(NullLogger<OsInfoProvider>.Instance).GetOsInfo();
        r.IsAvailable.Should().BeTrue(r.UnavailableReason);
        var os = r.Value!;
        os.BuildNumber.Should().BeGreaterThanOrEqualTo(19041);
        os.ProductName.Should().NotBeNullOrWhiteSpace();
        os.Architecture.Should().NotBeNullOrWhiteSpace();
        os.MachineName.Should().NotBeNullOrWhiteSpace();
        os.UpdateBuildRevision.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Windows_11_naming_when_build_is_22000_or_higher()
    {
        var os = new OsInfoProvider(NullLogger<OsInfoProvider>.Instance).GetOsInfo().Value!;
        if (os.BuildNumber >= 22000)
        {
            os.IsWindows11.Should().BeTrue();
            os.ProductName.Should().StartWith("Windows 11");
            os.ProductName.Should().NotStartWith("Windows 10");
        }
        else
        {
            os.IsWindows11.Should().BeFalse();
            os.ProductName.Should().NotStartWith("Windows 11");
        }
    }
}

public class RecycleBinInfoTests
{
    [Fact]
    public void Query_is_available_and_non_negative()
    {
        var r = new RecycleBinInfo(NullLogger<RecycleBinInfo>.Instance).Query();
        r.IsAvailable.Should().BeTrue(r.UnavailableReason);
        // ulong values cannot be negative; assert they were populated without throwing.
        r.Value.Should().NotBeNull();
    }
}

public class PrivilegeInfoTests
{
    [Fact]
    public void Returns_available_status_with_integrity_level()
    {
        var r = new PrivilegeInfo(NullLogger<PrivilegeInfo>.Instance).GetCurrent();
        r.IsAvailable.Should().BeTrue(r.UnavailableReason);
        var s = r.Value!;
        s.UserName.Should().NotBeNullOrWhiteSpace();
        s.IntegrityLevel.Should().NotBeNullOrWhiteSpace();
        s.IntegrityLevel.Should().BeOneOf("Untrusted", "Low", "Medium", "High", "System", "Unknown");
    }

    [Fact]
    public void Elevation_agrees_with_independent_windows_principal_check()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var independentElevated = principal.IsInRole(WindowsBuiltInRole.Administrator); // true only for an elevated admin token

        var s = new PrivilegeInfo(NullLogger<PrivilegeInfo>.Instance).GetCurrent().Value!;

        // IsInRole(Administrator) is true only when the admin SID is enabled, i.e. the token is an elevated admin token.
        s.IsElevated.Should().Be(independentElevated);
        if (!s.IsElevated)
        {
            new[] { "High", "System" }.Should().NotContain(s.IntegrityLevel);
        }
        s.UserName.Should().Be(identity.Name);
    }
}
