using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CMBuyerStudio.Desktop.Converters;

public sealed class StepArrowVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2)
        {
            return Visibility.Collapsed;
        }

        if (values[0] is not int index || values[1] is not int totalItems)
        {
            return Visibility.Collapsed;
        }

        return index < totalItems - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();
}
