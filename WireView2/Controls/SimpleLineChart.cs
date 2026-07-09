using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WireView2.ViewModels;

namespace WireView2.Controls;

/// <summary>Lightweight time-series chart drawn directly with DrawingContext.
/// Replaces LiveCharts' CartesianChart for the monitoring/logging graphs (ported
/// from the upstream 1.0.7 Windows client). X values are seconds; labels switch
/// to minutes/hours as the window grows. Hovering snaps a crosshair to the
/// nearest sample and shows a per-series legend tooltip.</summary>
public sealed class SimpleLineChart : Control
{
    public static readonly StyledProperty<SimpleChartViewModel?> ChartProperty =
        AvaloniaProperty.Register<SimpleLineChart, SimpleChartViewModel?>(nameof(Chart));

    public static readonly StyledProperty<double> XTickIntervalProperty =
        AvaloniaProperty.Register<SimpleLineChart, double>(nameof(XTickInterval), 1.0);

    public static readonly StyledProperty<double> YTickIntervalProperty =
        AvaloniaProperty.Register<SimpleLineChart, double>(nameof(YTickInterval), 10.0);

    public static readonly StyledProperty<IReadOnlyDictionary<string, Color>?> SeriesColorsProperty =
        AvaloniaProperty.Register<SimpleLineChart, IReadOnlyDictionary<string, Color>?>(nameof(SeriesColors));

    private static readonly Color[] FallbackPalette =
    {
        Colors.Lime, Colors.Cyan, Colors.Orange, Colors.Magenta, Colors.Yellow,
        Colors.DeepSkyBlue, Colors.White, Colors.Chartreuse, Colors.Gold, Colors.HotPink,
    };

    private SimpleChartViewModel? _subscribedChart;
    private readonly Dictionary<SimpleChartViewModel.Series, NotifyCollectionChangedEventHandler> _pointsChangedHandlers = new();

    private Point? _lastPointerPosition;
    private bool _isPointerInPlot;

    private const double PadLeft = 40.0;
    private const double PadTop = 16.0;
    private const double PadRight = 10.0;
    private const double PadBottom = 28.0;
    private const double HoverSnapMaxDistancePx = 50.0;

    public SimpleChartViewModel? Chart
    {
        get => GetValue(ChartProperty);
        set => SetValue(ChartProperty, value);
    }

    public double XTickInterval
    {
        get => GetValue(XTickIntervalProperty);
        set => SetValue(XTickIntervalProperty, value);
    }

    public double YTickInterval
    {
        get => GetValue(YTickIntervalProperty);
        set => SetValue(YTickIntervalProperty, value);
    }

    public IReadOnlyDictionary<string, Color>? SeriesColors
    {
        get => GetValue(SeriesColorsProperty);
        set => SetValue(SeriesColorsProperty, value);
    }

    static SimpleLineChart()
    {
        AffectsRender<SimpleLineChart>(ChartProperty, XTickIntervalProperty,
            YTickIntervalProperty, SeriesColorsProperty);
    }

    public SimpleLineChart()
    {
        PointerExited += OnPointerExited;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChartProperty)
            SubscribeToChart(change.NewValue as SimpleChartViewModel);
    }

    private void SubscribeToChart(SimpleChartViewModel? chart)
    {
        if (_subscribedChart == chart) return;
        UnsubscribeFromChart();
        _subscribedChart = chart;
        if (_subscribedChart == null) return;

        _subscribedChart.PropertyChanged += OnChartPropertyChanged;
        foreach (var series in _subscribedChart.SeriesItems)
            SubscribeToSeriesPoints(series);
    }

    private void UnsubscribeFromChart()
    {
        if (_subscribedChart == null) return;
        _subscribedChart.PropertyChanged -= OnChartPropertyChanged;
        foreach (var (series, handler) in _pointsChangedHandlers.ToArray())
        {
            series.Points.CollectionChanged -= handler;
            _pointsChangedHandlers.Remove(series);
        }
        _subscribedChart = null;
    }

    private void SubscribeToSeriesPoints(SimpleChartViewModel.Series series)
    {
        if (_pointsChangedHandlers.ContainsKey(series)) return;
        NotifyCollectionChangedEventHandler handler = delegate { InvalidateOnUiThread(); };
        _pointsChangedHandlers[series] = handler;
        series.Points.CollectionChanged += handler;
    }

    private void OnChartPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SimpleChartViewModel.SeriesItems) && _subscribedChart != null)
        {
            foreach (var series in _subscribedChart.SeriesItems)
                SubscribeToSeriesPoints(series);
        }
        InvalidateOnUiThread();
    }

    private void InvalidateOnUiThread()
    {
        if (Dispatcher.UIThread.CheckAccess())
            InvalidateVisual();
        else
            Dispatcher.UIThread.Post(InvalidateVisual);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _lastPointerPosition = e.GetPosition(this);
        UpdateHover(_lastPointerPosition.Value);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _lastPointerPosition = e.GetPosition(this);
        UpdateHover(_lastPointerPosition.Value);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        _lastPointerPosition = null;
        _isPointerInPlot = false;
        InvalidateVisual();
    }

    private void UpdateHover(Point pos)
    {
        if (Chart == null)
        {
            bool wasInPlot = _isPointerInPlot;
            _isPointerInPlot = false;
            if (wasInPlot) InvalidateOnUiThread();
            return;
        }

        var bounds = new Rect(Bounds.Size);
        var plot = GetPlotRect(bounds);
        bool wasIn = _isPointerInPlot;
        _isPointerInPlot = plot.Width > 0.0 && plot.Height > 0.0 && plot.Contains(pos);
        if (_isPointerInPlot || wasIn != _isPointerInPlot)
            InvalidateOnUiThread();
    }

    private static Rect GetPlotRect(Rect bounds) => new(
        bounds.X + PadLeft, bounds.Y + PadTop,
        Math.Max(0.0, bounds.Width - PadLeft - PadRight),
        Math.Max(0.0, bounds.Height - PadTop - PadBottom));

    private static bool TryGetNearestSeriesXWithinPixels(SimpleChartViewModel chart,
        IReadOnlyDictionary<string, Color>? seriesColors, Rect plot, double targetCanvasX,
        double xMin, double xMax, double maxDistancePx, out double nearestX)
    {
        bool found = false;
        nearestX = xMin;
        double bestDistance = double.MaxValue;

        foreach (var series in chart.SeriesItems)
        {
            // A non-empty color map doubles as the enabled-series filter.
            if (seriesColors is { Count: > 0 } && !seriesColors.ContainsKey(series.Key))
                continue;

            foreach (var point in series.Points)
            {
                if (point.X < xMin || point.X > xMax) continue;
                double canvasX = plot.Left + (point.X - xMin) / (xMax - xMin) * plot.Width;
                double distance = Math.Abs(canvasX - targetCanvasX);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    nearestX = point.X;
                    found = true;
                }
            }
        }
        return found && bestDistance <= maxDistancePx;
    }

    private static double GetNiceStep(double rawStep)
    {
        if (rawStep <= 0.0 || !double.IsFinite(rawStep)) return 1.0;
        double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(rawStep)));
        double normalized = rawStep / magnitude;
        double nice = normalized <= 1.0 ? 1.0 : normalized <= 2.0 ? 2.0 : normalized <= 5.0 ? 5.0 : 10.0;
        return nice * magnitude;
    }

    private static double ComputeXTickInterval(double xMin, double xMax, double plotWidth,
        double configuredMinInterval)
    {
        double range = Math.Abs(xMax - xMin);
        if (range <= 0.0 || !double.IsFinite(range)) return 1.0;

        int targetTicks = (int)Math.Clamp(Math.Floor(plotWidth / 90.0), 3.0, 12.0);
        double step = GetNiceStep(range / Math.Max(1, targetTicks - 1));
        return configuredMinInterval > 0.0 && double.IsFinite(configuredMinInterval)
            ? Math.Max(configuredMinInterval, step)
            : step;
    }

    private static string FormatXLabel(double xValueSeconds, double totalRangeSeconds)
    {
        double range = Math.Abs(totalRangeSeconds);
        if (range >= 7200.0) return $"{xValueSeconds / 3600.0:0.##}h";
        if (range >= 120.0) return $"{xValueSeconds / 60.0:0.##}m";
        return $"{xValueSeconds:0.##}s";
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds); // hit-test surface

        var chart = Chart;
        if (chart == null) return;

        var plot = GetPlotRect(bounds);
        double xMin = chart.XMin, xMax = chart.XMax;
        double yMin = chart.YMin, yMax = chart.YMax;
        if (xMax <= xMin) xMax = xMin + 1.0;
        if (yMax <= yMin) yMax = yMin + 1.0;

        var axisPen = new Pen(Brushes.Gray);
        var tickPen = new Pen(Brushes.DimGray);
        var labelBrush = Brushes.LightGray;

        // Y axis + ticks
        context.DrawLine(axisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        double yStep = YTickInterval;
        if (yStep > 0.0 && double.IsFinite(yStep))
        {
            for (double yv = Math.Ceiling(yMin / yStep) * yStep; yv <= yMax + yStep * 0.0001; yv += yStep)
            {
                double cy = plot.Bottom - (yv - yMin) / (yMax - yMin) * plot.Height;
                context.DrawLine(tickPen, new Point(plot.Left - 5.0, cy), new Point(plot.Left, cy));
                var label = new FormattedText(yv.ToString("0.##", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12.0, labelBrush);
                context.DrawText(label, new Point(plot.Left - 5.0 - label.Width - 4.0, cy - label.Height / 2.0));
            }
        }

        // X axis + ticks
        context.DrawLine(axisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        double xStep = ComputeXTickInterval(xMin, xMax, plot.Width, XTickInterval);
        if (xStep > 0.0 && double.IsFinite(xStep))
        {
            const int maxTicks = 1000;
            double totalRange = xMax - xMin;
            int drawn = 0;
            for (double xv = Math.Ceiling(xMin / xStep) * xStep;
                 xv <= xMax + xStep * 0.0001 && drawn < maxTicks;
                 xv += xStep, drawn++)
            {
                double cx = plot.Left + (xv - xMin) / (xMax - xMin) * plot.Width;
                context.DrawLine(tickPen, new Point(cx, plot.Bottom), new Point(cx, plot.Bottom + 5.0));
                var label = new FormattedText(FormatXLabel(xv, totalRange),
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12.0, labelBrush);
                context.DrawText(label, new Point(cx - label.Width / 2.0, plot.Bottom + 5.0 + 2.0));
            }
        }

        // Series polylines
        int paletteIndex = 0;
        var resolvedColors = new Dictionary<string, Color>();
        var seriesColors = SeriesColors;
        foreach (var series in chart.SeriesItems)
        {
            if (seriesColors != null && !seriesColors.ContainsKey(series.Key))
                continue;

            var visible = series.Points.Where(p => p.X >= xMin && p.X <= xMax).ToList();
            if (visible.Count < 2) continue;

            Color color = seriesColors != null && seriesColors.TryGetValue(series.Key, out var mapped)
                ? mapped
                : FallbackPalette[paletteIndex++ % FallbackPalette.Length];
            resolvedColors[series.Key] = color;

            var linePen = new Pen(new SolidColorBrush(color), 2.0);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                for (int i = 0; i < visible.Count; i++)
                {
                    var p = visible[i];
                    double cx = plot.Left + (p.X - xMin) / (xMax - xMin) * plot.Width;
                    double cy = plot.Bottom - (p.Y - yMin) / (yMax - yMin) * plot.Height;
                    if (i == 0) g.BeginFigure(new Point(cx, cy), isFilled: false);
                    else g.LineTo(new Point(cx, cy));
                }
            }
            context.DrawGeometry(null, linePen, geometry);
        }

        // Hover crosshair + tooltip legend
        if (!_isPointerInPlot || _lastPointerPosition is not { } pointer)
            return;

        double targetX = Math.Clamp(pointer.X, plot.Left, plot.Right);
        if (!TryGetNearestSeriesXWithinPixels(chart, SeriesColors, plot, targetX, xMin, xMax,
                HoverSnapMaxDistancePx, out double hoverX))
            return;

        double hoverCanvasX = plot.Left + (hoverX - xMin) / (xMax - xMin) * plot.Width;
        context.DrawLine(new Pen(Brushes.White), new Point(hoverCanvasX, plot.Top), new Point(hoverCanvasX, plot.Bottom));

        var legendRows = new List<(FormattedText Text, Color Color)>();
        foreach (var series in chart.SeriesItems)
        {
            if (SeriesColors is { Count: > 0 } && !SeriesColors.ContainsKey(series.Key))
                continue;
            var points = series.Points;
            if (points.Count == 0) continue;

            var nearest = points.Aggregate((a, b) => Math.Abs(a.X - hoverX) <= Math.Abs(b.X - hoverX) ? a : b);
            double cy = plot.Bottom - (nearest.Y - yMin) / (yMax - yMin) * plot.Height;
            context.DrawGeometry(Brushes.White, null,
                new EllipseGeometry(new Rect(hoverCanvasX - 3.0, cy - 3.0, 6.0, 6.0)));

            var text = new FormattedText($"{series.Name}: {nearest.Y:0.###}",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12.0, Brushes.White);
            legendRows.Add((text, resolvedColors.TryGetValue(series.Key, out var c) ? c : Colors.White));
        }
        if (legendRows.Count == 0) return;

        double contentWidth = legendRows.Max(e => 16.0 + e.Text.Width);
        double contentHeight = legendRows.Sum(e => e.Text.Height) + 3.0 * (legendRows.Count - 1);
        double boxWidth = contentWidth + 12.0;
        double boxHeight = contentHeight + 12.0;
        double boxX = Math.Clamp(hoverCanvasX + 10.0, plot.Left, plot.Right - boxWidth);
        double boxY = Math.Clamp(plot.Top + 6.0, plot.Top, plot.Bottom - boxHeight);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(235, 18, 18, 18)),
            new Pen(Brushes.Gray), new Rect(boxX, boxY, boxWidth, boxHeight));

        double rowY = boxY + 6.0;
        foreach (var (text, color) in legendRows)
        {
            double swatchY = rowY + Math.Max(0.0, (text.Height - 10.0) / 2.0);
            context.DrawRectangle(new SolidColorBrush(color), new Pen(Brushes.Black),
                new Rect(boxX + 6.0, swatchY, 10.0, 10.0));
            context.DrawText(text, new Point(boxX + 6.0 + 10.0 + 6.0, rowY));
            rowY += text.Height + 3.0;
        }
    }
}
