using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using WireView2.Device;

namespace WireView2.Converters;

/// <summary>Shows an averaging window as a duration ("22 ms", "2.8 s") instead of the
/// enum name. A value the firmware reports but the app does not know is shown by number.</summary>
public sealed class AveragingNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not WireViewPro2Device.AVG avg)
            return value?.ToString();
        string name = avg.ToString();
        if (name.StartsWith("AVG_", StringComparison.Ordinal) && name.EndsWith("MS", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(4, name.Length - 6), NumberStyles.None, CultureInfo.InvariantCulture, out int ms))
        {
            return ms < 1000
                ? ms.ToString(CultureInfo.InvariantCulture) + " ms"
                : (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }
        return "Unknown (" + ((int)avg).ToString(CultureInfo.InvariantCulture) + ")";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
