using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace WireView2.Converters;

public sealed class BoolToOkFaultBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return new SolidColorBrush(Color.Parse("#FFE54225")); // Fault = red
        return new SolidColorBrush(Color.Parse("#FF2E7D32"));     // OK = green
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
