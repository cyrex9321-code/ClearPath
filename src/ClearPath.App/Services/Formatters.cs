using System.Globalization;
using ClearPath.Core.Models;

namespace ClearPath.App.Services;

/// <summary>Display formatting shared by all view models.</summary>
internal static class Formatters
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>1024-based, one decimal (bytes below 1 KB are shown as whole bytes).</summary>
    public static string Bytes(ulong bytes)
    {
        if (bytes < 1024) return string.Create(CultureInfo.CurrentCulture, $"{bytes} B");
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.CurrentCulture, $"{value:0.0} {Units[unit]}");
    }

    public static string Bytes(long bytes) => Bytes(bytes < 0 ? 0UL : (ulong)bytes);

    /// <summary>One decimal, e.g. "12.3 %".</summary>
    public static string Percent(double percent) => string.Create(CultureInfo.CurrentCulture, $"{percent:0.0} %");

    public static string Timestamp(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Count(ulong value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>The reading's value formatted, or its unavailability reason - never a placeholder number.</summary>
    public static string Show<T>(Reading<T> reading, Func<T, string> format) =>
        reading.IsAvailable && reading.Value is not null
            ? format(reading.Value)
            : reading.UnavailableReason ?? "Unavailable";
}
