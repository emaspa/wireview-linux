using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WireView2.Controls;

/// <summary>Half-circle (180°) gauge drawn directly with DrawingContext — replaces
/// the LiveCharts PieChart gauges on the Overview page (ported from the upstream
/// 1.0.7 Windows client). Repaints are throttled: identical values only repaint
/// every 100th update.</summary>
public sealed class SimpleGaugeChart : Control
{
    private const int SameValueUpdateInterval = 100;

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<SimpleGaugeChart, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SimpleGaugeChart, double>(nameof(Value));

    public static readonly StyledProperty<double> MaxProperty =
        AvaloniaProperty.Register<SimpleGaugeChart, double>(nameof(Max), 100.0);

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SimpleGaugeChart, string?>(nameof(Label));

    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<SimpleGaugeChart, string?>(nameof(Unit));

    public static readonly StyledProperty<IBrush> AccentBrushProperty =
        AvaloniaProperty.Register<SimpleGaugeChart, IBrush>(nameof(AccentBrush), Brushes.DeepSkyBlue);

    public static readonly StyledProperty<bool> ShowLabelProperty =
        AvaloniaProperty.Register<SimpleGaugeChart, bool>(nameof(ShowLabel), true);

    private double _lastRenderedValue = double.NaN;
    private int _sameValueUpdateCounter;

    private IBrush LabelBrush => Foreground ?? Brushes.LightGray;
    private IBrush ValueBrush => Foreground ?? Brushes.White;

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Max
    {
        get => GetValue(MaxProperty);
        set => SetValue(MaxProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public IBrush AccentBrush
    {
        get => GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public bool ShowLabel
    {
        get => GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    static SimpleGaugeChart()
    {
        AffectsRender<SimpleGaugeChart>(MaxProperty, LabelProperty, UnitProperty,
            AccentBrushProperty, ShowLabelProperty, ForegroundProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ValueProperty)
            return;

        // Live telemetry pushes Value every poll even when it hasn't moved —
        // skip those repaints (but repaint every 100th as a safety net).
        if (Equals(change.GetNewValue<double>(), _lastRenderedValue))
        {
            _sameValueUpdateCounter++;
            if (_sameValueUpdateCounter % SameValueUpdateInterval != 0)
                return;
        }
        else
        {
            _sameValueUpdateCounter = 0;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _lastRenderedValue = Value;

        var bounds = new Rect(Bounds.Size);

        double labelHeight = 0.0;
        if (ShowLabel && !string.IsNullOrWhiteSpace(Label))
        {
            labelHeight = new FormattedText(Label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 18.0, LabelBrush)
            {
                TextAlignment = TextAlignment.Center,
            }.Height;
        }

        const double bottomPad = 16.0;
        double labelGap = labelHeight > 0.0 ? 6.0 : 0.0;
        double availableHeight = bounds.Height - labelHeight - labelGap;
        double radius = Math.Max(0.0, Math.Min(bounds.Width / 2.0 - bottomPad, availableHeight - bottomPad * 2.0));
        if (radius <= 1.0) return;

        var center = new Point(bounds.Center.X, bounds.Bottom - bottomPad);
        double thickness = Math.Max(18.0, radius * 0.28);
        double arcRadius = Math.Max(0.0, radius - thickness / 2.0);

        var backgroundPen = new Pen(new SolidColorBrush(Color.FromRgb(220, 220, 220)), thickness)
        {
            LineCap = PenLineCap.Flat,
        };
        var accentPen = new Pen(AccentBrush, thickness)
        {
            LineCap = PenLineCap.Flat,
        };

        double max = Max;
        if (max <= 0.0) max = 1.0;
        double ratio = Value / max;
        if (!double.IsFinite(ratio)) ratio = 0.0;
        ratio = Math.Clamp(ratio, 0.0, 1.0);

        DrawArc(context, center, arcRadius, 180.0, -180.0, backgroundPen);
        DrawArc(context, center, arcRadius, 180.0, -180.0 * ratio, accentPen);

        string valueText = string.IsNullOrWhiteSpace(Unit)
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.#}", Value)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.#} {1}", Value, Unit);
        var value = new FormattedText(valueText, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default,
            Math.Clamp(arcRadius * 0.35, 14.0, 32.0), ValueBrush)
        {
            TextAlignment = TextAlignment.Center,
        };
        var valueCenter = new Point(bounds.Center.X, center.Y * 1.1 - arcRadius * 0.55);
        context.DrawText(value, new Point(valueCenter.X - value.Width / 2.0, valueCenter.Y - value.Height / 2.0));

        if (ShowLabel && !string.IsNullOrWhiteSpace(Label))
        {
            var label = new FormattedText(Label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 18.0, LabelBrush)
            {
                TextAlignment = TextAlignment.Center,
            };
            context.DrawText(label, new Point(bounds.Center.X - label.Width / 2.0, 5.0));
        }
    }

    private static void DrawArc(DrawingContext context, Point center, double radius,
        double startDeg, double sweepDeg, Pen pen)
    {
        if (radius <= 0.0 || Math.Abs(sweepDeg) <= 0.01)
            return;

        // ArcTo degenerates at exactly 180° (start == end point) — nudge it.
        if (Math.Abs(Math.Abs(sweepDeg) - 180.0) < 0.0001)
            sweepDeg = Math.Sign(sweepDeg) * 179.9999;

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            double startRad = DegreesToRadians(startDeg);
            double endRad = DegreesToRadians(startDeg + sweepDeg);
            var start = new Point(center.X + radius * Math.Cos(startRad), center.Y - radius * Math.Sin(startRad));
            var end = new Point(center.X + radius * Math.Cos(endRad), center.Y - radius * Math.Sin(endRad));
            bool isLargeArc = Math.Abs(sweepDeg) > 180.0;
            var direction = sweepDeg >= 0.0 ? SweepDirection.CounterClockwise : SweepDirection.Clockwise;
            g.BeginFigure(start, isFilled: false);
            g.ArcTo(end, new Size(radius, radius), 0.0, isLargeArc, direction);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}
