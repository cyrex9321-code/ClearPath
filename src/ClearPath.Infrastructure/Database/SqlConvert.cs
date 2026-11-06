using System.Globalization;
using ClearPath.Core.FileSystem;

namespace ClearPath.Infrastructure.Database;

internal static class SqlConvert
{
    internal static string Hex(UInt128 value) => value.ToString("X32", CultureInfo.InvariantCulture);

    internal static UInt128 ParseHex(string hex) => UInt128.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    internal static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    internal static object OrNull(string? value) => (object?)value ?? DBNull.Value;
    internal static object OrNull(long? value) => (object?)value ?? DBNull.Value;
    internal static object OrNull(ulong? value) => value is { } v ? (long)v : DBNull.Value;
}
