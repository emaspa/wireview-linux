using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using WireView2.Services;

namespace WireView2.Converters;

/// <summary>Shows a theme mode by its user-facing name ("Noctua Light").</summary>
public sealed class ThemeModeNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppSettings.ThemeMode mode ? EditionThemeService.DisplayName(mode) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
