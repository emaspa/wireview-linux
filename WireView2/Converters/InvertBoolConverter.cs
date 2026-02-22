using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace WireView2.Converters;

public sealed class InvertBoolConverter : IValueConverter
{
    public static readonly InvertBoolConverter Instance = new InvertBoolConverter();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool flag)
            return !flag;
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool flag)
            return !flag;
        return value;
    }
}
