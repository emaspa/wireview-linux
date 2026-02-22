using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace WireView2.Converters;

public sealed class BoolToOkFaultConverter : IValueConverter
{
    public static readonly BoolToOkFaultConverter Instance = new BoolToOkFaultConverter();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return "FAULT";
        return "OK";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
