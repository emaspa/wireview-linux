using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using WireView2.Device;
using WireView2.Services;

namespace WireView2;

/// <summary>
/// The right-click dropdown of a tray power icon: one device's live readings
/// (total power, current, average voltage, the six pins, the temperatures that
/// are present, the cable rating), then "Show window". The items are written
/// only just before the menu shows and by the throttled tray refresh, and only
/// when their text changed: every write makes Avalonia's DBus menu exporter
/// re-publish the layout, which flashes an open menu.
/// </summary>
internal sealed class TrayValuesMenu
{
    private readonly NativeMenuItem? _title;
    private readonly NativeMenuItem _totalPower = Row();
    private readonly NativeMenuItem _totalCurrent = Row();
    private readonly NativeMenuItem _avgVoltage = Row();
    private readonly NativeMenuItem[] _pins = Enumerable.Range(0, 6).Select(_ => Row()).ToArray();
    private readonly NativeMenuItem _tempIn = Row();
    private readonly NativeMenuItem _tempOut = Row();
    private readonly NativeMenuItem _tempExt1 = Row();
    private readonly NativeMenuItem _tempExt2 = Row();
    private readonly NativeMenuItem _cableRating = Row();

    /// <summary>Absent temperature sensors get no row.</summary>
    public TemperatureSensorPresence TempPresence { get; } = new();

    public NativeMenu Menu { get; } = new();

    /// <param name="showWindow">What "Show window" does.</param>
    /// <param name="refresh">Called just before the menu shows, to write fresh values.</param>
    /// <param name="withTitle">A first row naming the device (for one icon per device).</param>
    public TrayValuesMenu(Action showWindow, Action refresh, bool withTitle = false)
    {
        if (withTitle)
        {
            _title = Row();
            Menu.Items.Add(_title);
            Menu.Items.Add(new NativeMenuItemSeparator());
        }
        Menu.Items.Add(_totalPower);
        Menu.Items.Add(_totalCurrent);
        Menu.Items.Add(_avgVoltage);
        Menu.Items.Add(new NativeMenuItemSeparator());
        foreach (var pin in _pins) Menu.Items.Add(pin);
        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(_tempIn);
        Menu.Items.Add(_tempOut);
        Menu.Items.Add(_tempExt1);
        Menu.Items.Add(_tempExt2);
        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(_cableRating);
        Menu.Items.Add(new NativeMenuItemSeparator());
        var show = new NativeMenuItem("Show window");
        show.Click += (_, _) => showWindow();
        Menu.Items.Add(show);
        Menu.NeedsUpdate += (_, _) => refresh();
        Menu.Opening += (_, _) => refresh();
    }

    private static NativeMenuItem Row() => new() { IsEnabled = false };

    public void SetTitle(string title)
    {
        if (_title != null) SetHeaderIfChanged(_title, title);
    }

    public void Update(DeviceData? d, bool connected)
    {
        if (!connected || d == null)
        {
            SetHeaderIfChanged(_totalPower, "Disconnected");
            SetHeaderIfChanged(_totalCurrent, "Total current: N/A");
            SetHeaderIfChanged(_avgVoltage, "Average voltage: N/A");
            for (int i = 0; i < _pins.Length; i++)
                SetHeaderIfChanged(_pins[i], $"Pin {i + 1}: N/A");
            SetHeaderIfChanged(_tempIn, "Temp onboard in: N/A");
            SetHeaderIfChanged(_tempOut, "Temp onboard out: N/A");
            SetHeaderIfChanged(_tempExt1, "Temp external 1: N/A");
            SetHeaderIfChanged(_tempExt2, "Temp external 2: N/A");
            SetHeaderIfChanged(_cableRating, "Cable rating: N/A");
            SetVisibleIfChanged(_tempIn, true);
            SetVisibleIfChanged(_tempOut, true);
            SetVisibleIfChanged(_tempExt1, true);
            SetVisibleIfChanged(_tempExt2, true);
            return;
        }

        var ci = CultureInfo.InvariantCulture;
        SetHeaderIfChanged(_totalPower, $"Total power: {d.SumPowerW.ToString("0.0", ci)} W");
        SetHeaderIfChanged(_totalCurrent, $"Total current: {d.SumCurrentA.ToString("0.00", ci)} A");
        SetHeaderIfChanged(_avgVoltage, $"Average voltage: {d.PinVoltage.Average().ToString("0.00", ci)} V");
        for (int i = 0; i < _pins.Length; i++)
        {
            double v = d.PinVoltage[i], a = d.PinCurrent[i];
            SetHeaderIfChanged(_pins[i],
                $"Pin {i + 1}: {v.ToString("0.00", ci)} V  {a.ToString("0.00", ci)} A  {(v * a).ToString("0.0", ci)} W");
        }
        SetHeaderIfChanged(_tempIn, $"Temp onboard in: {FormatTemp(d.OnboardTempInC)}");
        SetHeaderIfChanged(_tempOut, $"Temp onboard out: {FormatTemp(d.OnboardTempOutC)}");
        SetHeaderIfChanged(_tempExt1, $"Temp external 1: {FormatTemp(d.ExternalTemp1C)}");
        SetHeaderIfChanged(_tempExt2, $"Temp external 2: {FormatTemp(d.ExternalTemp2C)}");
        SetVisibleIfChanged(_tempIn, TempPresence.IsPresent(0));
        SetVisibleIfChanged(_tempOut, TempPresence.IsPresent(1));
        SetVisibleIfChanged(_tempExt1, TempPresence.IsPresent(2));
        SetVisibleIfChanged(_tempExt2, TempPresence.IsPresent(3));
        SetHeaderIfChanged(_cableRating, d.PsuCapabilityW > 0
            ? $"Cable rating: {d.PsuCapabilityW} W"
            : "Cable rating: N/A");
    }

    private static string FormatTemp(double t) =>
        t > -100.0 && t < 200.0 ? $"{t.ToString("0.#", CultureInfo.InvariantCulture)} °C" : "N/A";

    internal static void SetHeaderIfChanged(NativeMenuItem item, string text)
    {
        if (!string.Equals(item.Header as string, text, StringComparison.Ordinal))
            item.Header = text;
    }

    internal static void SetVisibleIfChanged(NativeMenuItem item, bool visible)
    {
        if (item.IsVisible != visible)
            item.IsVisible = visible;
    }
}
