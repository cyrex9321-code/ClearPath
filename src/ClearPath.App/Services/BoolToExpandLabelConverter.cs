using System.Globalization;
using System.Windows.Data;

namespace ClearPath.App.Services;

/// <summary>"Hide files" when true (expanded), "Show files" otherwise.</summary>
public sealed class BoolToExpandLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "Hide files" : "Show files";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
