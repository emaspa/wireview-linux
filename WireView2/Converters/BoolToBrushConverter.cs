using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace WireView2.Converters;

public sealed class BoolToBrushConverter : IValueConverter
{
    public IBrush TrueBrush { get; set; } = Brushes.Green;
    public IBrush FalseBrush { get; set; } = Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return TrueBrush;
        return FalseBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
