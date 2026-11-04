using ClearPath.Core.Models;

namespace ClearPath.Core.Tests;

public class ReadingTests
{
    [Fact]
    public void Of_is_available_with_value_and_no_reason()
    {
        var r = Reading<double>.Of(42.5);
        r.IsAvailable.Should().BeTrue();
        r.Value.Should().Be(42.5);
        r.UnavailableReason.Should().BeNull();
    }

    [Fact]
    public void Of_zero_is_still_available()
    {
        var r = Reading<int>.Of(0);
        r.IsAvailable.Should().BeTrue();
        r.Value.Should().Be(0);
    }

    [Fact]
    public void Unavailable_carries_reason_and_no_value()
    {
        var r = Reading<int>.Unavailable("Measuring…");
        r.IsAvailable.Should().BeFalse();
        r.Value.Should().Be(default(int));
        r.UnavailableReason.Should().Be("Measuring…");
    }

    [Fact]
    public void Unavailable_reference_type_has_null_value()
    {
        Reading<string>.Unavailable("x").Value.Should().BeNull();
    }

    [Fact]
    public void Default_struct_is_not_available()
    {
        default(Reading<int>).IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Value_equality()
    {
        Reading<int>.Of(1).Should().Be(Reading<int>.Of(1));
        Reading<int>.Of(1).Should().NotBe(Reading<int>.Of(2));
        Reading<int>.Unavailable("a").Should().Be(Reading<int>.Unavailable("a"));
        Reading<int>.Unavailable("a").Should().NotBe(Reading<int>.Unavailable("b"));
        Reading<int>.Of(0).Should().NotBe(Reading<int>.Unavailable("a"));
    }

    [Fact]
    public void ToString_renders_value_or_reason()
    {
        Reading<int>.Of(7).ToString().Should().Be("7");
        Reading<int>.Unavailable("nope").ToString().Should().Be("Unavailable (nope)");
    }
}

public class ModelTests
{
    [Fact]
    public void AppSettings_defaults()
    {
        var s = new AppSettings();
        s.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
        s.Theme.Should().Be(ThemePreference.System);
        s.Exclusions.Should().BeEmpty();
        s.QuarantineRetentionDays.Should().Be(30);
        s.QuarantineLocation.Should().BeNull();
    }

    [Fact]
    public void MemoryStatus_used_is_total_minus_available()
    {
        new MemoryStatus(1000, 400, 60).UsedPhysicalBytes.Should().Be(600);
    }

    [Theory]
    [InlineData(100UL, 25UL, 0.75)]
    [InlineData(100UL, 100UL, 0.0)]
    [InlineData(100UL, 0UL, 1.0)]
    [InlineData(0UL, 0UL, 0.0)]
    public void DriveStatus_used_fraction(ulong total, ulong free, double expected)
    {
        var d = new DriveStatus(@"C:\", "L", "NTFS", DriveKind.Fixed, total, free);
        d.UsedBytes.Should().Be(total - free);
        d.UsedFraction.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(19045, false)]
    [InlineData(21999, false)]
    [InlineData(22000, true)]
    [InlineData(26200, true)]
    public void OsInfo_windows11_is_decided_by_build_number(int build, bool expected)
    {
        new OsInfo("Windows 10 Home", "25H2", build, 0, "X64", "M").IsWindows11.Should().Be(expected);
    }
}
