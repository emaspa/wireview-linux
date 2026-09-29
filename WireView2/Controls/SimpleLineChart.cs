using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using WireView2.ViewModels;

namespace WireView2.Controls;

/// <summary>Lightweight time-series chart drawn directly with DrawingContext.
/// Replaces LiveCharts' CartesianChart for the monitoring/logging graphs (ported
/// from the upstream 1.0.7 Windows client, render path from 1.0.8). X values are
/// seconds; labels switch to minutes/hours as the window grows. Hovering snaps a
/// crosshair to the nearest sample and shows a per-series legend tooltip.
///
/// Idle cost (upstream 1.0.8): one repaint per series snapshot instead of one per
/// added/removed point, cached pens, brushes and tick labels, no LINQ in Render,
/// min/max-per-pixel decimation of dense series, and hover repaints only when the
/// snapped sample changes instead of on every mouse move.</summary>
public sealed class SimpleLineChart : Control
{
    public static readonly StyledProperty<SimpleChartViewModel?> ChartProperty =
        AvaloniaProperty.Register<SimpleLineChart, SimpleChartViewModel?>(nameof(Chart));

    public static readonly StyledProperty<double> XTickIntervalProperty =
        AvaloniaProperty.Register<SimpleLineChart, double>(nameof(XTickInterval), 1.0);

    public static readonly StyledProperty<double> YTickIntervalProperty =
        AvaloniaProperty.Register<SimpleLineChart, double>(nameof(YTickInterval), 10.0);

    /// <summary>Series key → colour. A non-empty map doubles as the enabled-series
    /// filter (the Monitoring/Logging series toggles).</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<string, Color>?> SeriesColorsProperty =
        AvaloniaProperty.Register<SimpleLineChart, IReadOnlyDictionary<string, Color>?>(nameof(SeriesColors));

    private static readonly Color[] FallbackPalette =
    {
        Colors.Lime, Colors.Cyan, Colors.Orange, Colors.Magenta, Colors.Yellow,
        Colors.DeepSkyBlue, Colors.White, Colors.Chartreuse, Colors.Gold, Colors.HotPink,
    };

    private static readonly IPen AxisPen = new ImmutablePen(Brushes.Gray.ToImmutable());
    private static readonly IPen TickPen = new ImmutablePen(Brushes.DimGray.ToImmutable());
    private static readonly IPen HoverPen = new ImmutablePen(Brushes.White.ToImmutable());
    private static readonly IPen LegendBorderPen = new ImmutablePen(Brushes.Gray.ToImmutable());
    private static readonly IPen SwatchBorderPen = new ImmutablePen(Brushes.Black.ToImmutable());
    private static readonly IBrush LegendBackground = new ImmutableSolidColorBrush(Color.FromArgb(235, 18, 18, 18));

    private SimpleChartViewModel? _subscribedChart;
    private readonly HashSet<SimpleChartViewModel.Series> _subscribedSeries = new();
    private int _invalidatePosted;
    private bool _isAttached;

    private Point? _lastPointerPosition;
    private bool _isPointerInPlot;
    private bool _hasHoverSnap;
    private double _lastHoverSnapX;

    private readonly Dictionary<Color, IPen> _seriesPens = new();
    private readonly Dictionary<Color, IBrush> _swatchBrushes = new();
    private readonly Dictionary<string, FormattedText> _tickLabels = new();
    private (double XMin, double XMax, double YMin, double YMax, double Width, double Height) _tickLabelsKey;
    private readonly Dictionary<string, Color> _resolvedColors = new();

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
        if (change.Property == ChartProperty && _isAttached)
            SubscribeToChart(change.NewValue as SimpleChartViewModel);
    }

    // Listen to the view model only while on screen: a page view is rebuilt each
    // time its page is shown, and a chart left subscribed would keep reacting to
    // every update of the (long-lived) view model after its page was left.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        SubscribeToChart(Chart);
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttached = false;
        UnsubscribeFromChart();
    }

    private void SubscribeToChart(SimpleChartViewModel? chart)
    {
        if (_subscribedChart == chart) return;
        UnsubscribeFromChart();
        _subscribedChart = chart;
        if (_subscribedChart == null) return;

        _subscribedChart.PropertyChanged += OnChartPropertyChanged;
        foreach (var series in _subscribedChart.SeriesItems)
            SubscribeToSeries(series);
    }

    private void UnsubscribeFromChart()
    {
        if (_subscribedChart == null) return;
        _subscribedChart.PropertyChanged -= OnChartPropertyChanged;
        foreach (var series in _subscribedSeries)
            series.PropertyChanged -= OnSeriesPropertyChanged;
        _subscribedSeries.Clear();
        _subscribedChart = null;
    }

    private void SubscribeToSeries(SimpleChartViewModel.Series series)
    {
        if (_subscribedSeries.Add(series))
            series.PropertyChanged += OnSeriesPropertyChanged;
    }

    private void OnSeriesPropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateOnUiThread();

    private void OnChartPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SimpleChartViewModel.SeriesItems) && _subscribedChart != null)
        {
            foreach (var series in _subscribedChart.SeriesItems)
                SubscribeToSeries(series);
        }
        InvalidateOnUiThread();
    }

    private void InvalidateOnUiThread()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            InvalidateVisual();
            return;
        }
        // Coalesce: one queued repaint however many notifications arrive before it runs.
        if (Interlocked.Exchange(ref _invalidatePosted, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _invalidatePosted, 0);
                InvalidateVisual();
            });
        }
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
        bool wasShown = _isPointerInPlot || _hasHoverSnap;
        _isPointerInPlot = false;
        _hasHoverSnap = false;
        if (wasShown)
            InvalidateVisual();
    }

    /// <summary>Repaints only when the pointer enters or leaves the plot, or when
    /// the sample the crosshair snaps to changes.</summary>
    private void UpdateHover(Point pos)
    {
        var chart = Chart;
        if (chart == null)
        {
            bool wasInPlot = _isPointerInPlot;
            _isPointerInPlot = false;
            _hasHoverSnap = false;
            if (wasInPlot) InvalidateOnUiThread();
            return;
        }

        var plot = GetPlotRect(new Rect(Bounds.Size));
        bool wasIn = _isPointerInPlot;
        _isPointerInPlot = plot.Width > 0.0 && plot.Height > 0.0 && plot.Contains(pos);

        bool hasSnap = false;
        double snapX = 0.0;
        if (_isPointerInPlot)
        {
            double xMin = chart.XMin, xMax = chart.XMax;
            if (xMax <= xMin) xMax = xMin + 1.0;
            hasSnap = TryGetNearestSeriesXWithinPixels(chart, SeriesColors, plot,
                Math.Clamp(pos.X, plot.Left, plot.Right), xMin, xMax, HoverSnapMaxDistancePx, out snapX);
        }
        bool snapChanged = hasSnap != _hasHoverSnap || (hasSnap && snapX != _lastHoverSnapX);
        _hasHoverSnap = hasSnap;
        _lastHoverSnapX = snapX;
        if (wasIn != _isPointerInPlot || snapChanged)
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

            var points = series.Points;
            for (int i = 0; i < points.Count; i++)
            {
                var point = points[i];
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

    private IPen GetSeriesPen(Color color)
    {
        if (!_seriesPens.TryGetValue(color, out var pen))
        {
            pen = new ImmutablePen(new ImmutableSolidColorBrush(color), 2.0);
            _seriesPens[color] = pen;
        }
        return pen;
    }

    private IBrush GetSwatchBrush(Color color)
    {
        if (!_swatchBrushes.TryGetValue(color, out var brush))
        {
            brush = new ImmutableSolidColorBrush(color);
            _swatchBrushes[color] = brush;
        }
        return brush;
    }

    private FormattedText GetTickLabel(string text)
    {
        if (!_tickLabels.TryGetValue(text, out var label))
        {
            label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12.0, Brushes.LightGray);
            _tickLabels[text] = label;
        }
        return label;
    }

    /// <summary>Draws a dense run of points with at most four vertices per pixel
    /// column (first, min, max, last), which keeps the visible envelope.</summary>
    private static void DrawDecimated(StreamGeometryContext g, IReadOnlyList<SimpleChartViewModel.DataPoint> points,
        int first, int last, Rect plot, double xMin, double xScale, double yMin, double yScale)
    {
        Span<int> picks = stackalloc int[4];
        bool started = false;
        int i = first;
        while (i <= last)
        {
            double column = Math.Floor((points[i].X - xMin) * xScale);
            int minIdx = i, maxIdx = i;
            int j = i;
            for (; j <= last && Math.Floor((points[j].X - xMin) * xScale) == column; j++)
            {
                if (points[j].Y < points[minIdx].Y) minIdx = j;
                if (points[j].Y > points[maxIdx].Y) maxIdx = j;
            }
            int lastIdx = j - 1;

            int n = 0;
            picks[n++] = i;
            if (minIdx != i) picks[n++] = minIdx;
            if (maxIdx != i && maxIdx != minIdx) picks[n++] = maxIdx;
            if (lastIdx != i && lastIdx != minIdx && lastIdx != maxIdx) picks[n++] = lastIdx;
            picks[..n].Sort();

            for (int k = 0; k < n; k++)
            {
                var p = points[picks[k]];
                var pt = new Point(plot.Left + (p.X - xMin) * xScale, plot.Bottom - (p.Y - yMin) * yScale);
                if (!started) { g.BeginFigure(pt, isFilled: false); started = true; }
                else g.LineTo(pt);
            }
            i = j;
        }
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

        // Tick labels are cached until the axes or the plot size change.
        var labelsKey = (xMin, xMax, yMin, yMax, plot.Width, plot.Height);
        if (labelsKey != _tickLabelsKey)
        {
            _tickLabels.Clear();
            _tickLabelsKey = labelsKey;
        }

        // Y axis + ticks
        context.DrawLine(AxisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        double yStep = YTickInterval;
        if (yStep > 0.0 && double.IsFinite(yStep))
        {
            for (double yv = Math.Ceiling(yMin / yStep) * yStep; yv <= yMax + yStep * 0.0001; yv += yStep)
            {
                double cy = plot.Bottom - (yv - yMin) / (yMax - yMin) * plot.Height;
                context.DrawLine(TickPen, new Point(plot.Left - 5.0, cy), new Point(plot.Left, cy));
                // Stepping accumulates rounding error: print 0, not "-0".
                double shown = Math.Abs(yv) < yStep * 1e-6 ? 0.0 : yv;
                var label = GetTickLabel(shown.ToString("0.##", CultureInfo.InvariantCulture));
                context.DrawText(label, new Point(plot.Left - 5.0 - label.Width - 4.0, cy - label.Height / 2.0));
            }
        }

        // X axis + ticks
        context.DrawLine(AxisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
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
                context.DrawLine(TickPen, new Point(cx, plot.Bottom), new Point(cx, plot.Bottom + 5.0));
                var label = GetTickLabel(FormatXLabel(xv, totalRange));
                context.DrawText(label, new Point(cx - label.Width / 2.0, plot.Bottom + 5.0 + 2.0));
            }
        }

        // Series polylines
        int paletteIndex = 0;
        _resolvedColors.Clear();
        var seriesColors = SeriesColors;
        double xScale = plot.Width / (xMax - xMin);
        double yScale = plot.Height / (yMax - yMin);
        foreach (var series in chart.SeriesItems)
        {
            if (seriesColors != null && !seriesColors.ContainsKey(series.Key))
                continue;

            // Points are in ascending X: find the visible range from both ends.
            var points = series.Points;
            int first = 0;
            while (first < points.Count && points[first].X < xMin) first++;
            int last = points.Count - 1;
            while (last >= first && points[last].X > xMax) last--;
            if (last - first < 1) continue;

            Color color = seriesColors != null && seriesColors.TryGetValue(series.Key, out var mapped)
                ? mapped
                : FallbackPalette[paletteIndex++ % FallbackPalette.Length];
            _resolvedColors[series.Key] = color;

            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                if (last - first + 1 <= plot.Width * 2.0)
                {
                    for (int i = first; i <= last; i++)
                    {
                        var p = points[i];
                        var pt = new Point(plot.Left + (p.X - xMin) * xScale, plot.Bottom - (p.Y - yMin) * yScale);
                        if (i == first) g.BeginFigure(pt, isFilled: false);
                        else g.LineTo(pt);
                    }
                }
                else
                {
                    DrawDecimated(g, points, first, last, plot, xMin, xScale, yMin, yScale);
                }
            }
            context.DrawGeometry(null, GetSeriesPen(color), geometry);
        }

        // Hover crosshair + tooltip legend
        if (!_isPointerInPlot || _lastPointerPosition is not { } pointer)
            return;

        double targetX = Math.Clamp(pointer.X, plot.Left, plot.Right);
        if (!TryGetNearestSeriesXWithinPixels(chart, seriesColors, plot, targetX, xMin, xMax,
                HoverSnapMaxDistancePx, out double hoverX))
            return;

        double hoverCanvasX = plot.Left + (hoverX - xMin) / (xMax - xMin) * plot.Width;
        context.DrawLine(HoverPen, new Point(hoverCanvasX, plot.Top), new Point(hoverCanvasX, plot.Bottom));

        var legendRows = new List<(FormattedText Text, Color Color)>();
        double contentWidth = 0.0, contentHeight = 0.0;
        foreach (var series in chart.SeriesItems)
        {
            if (seriesColors is { Count: > 0 } && !seriesColors.ContainsKey(series.Key))
                continue;
            var points = series.Points;
            if (points.Count == 0) continue;

            var nearest = points[0];
            double best = Math.Abs(nearest.X - hoverX);
            for (int i = 1; i < points.Count; i++)
            {
                double d = Math.Abs(points[i].X - hoverX);
                if (d < best) { best = d; nearest = points[i]; }
            }
            double cy = plot.Bottom - (nearest.Y - yMin) / (yMax - yMin) * plot.Height;
            context.DrawGeometry(Brushes.White, null,
                new EllipseGeometry(new Rect(hoverCanvasX - 3.0, cy - 3.0, 6.0, 6.0)));

            var text = new FormattedText($"{series.Name}: {nearest.Y:0.###}",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12.0, Brushes.White);
            legendRows.Add((text, _resolvedColors.TryGetValue(series.Key, out var c) ? c : Colors.White));
            contentWidth = Math.Max(contentWidth, 16.0 + text.Width);
            contentHeight += text.Height;
        }
        if (legendRows.Count == 0) return;
        contentHeight += 3.0 * (legendRows.Count - 1);

        // Cap the box to the plot (upstream 1.0.8): with many enabled series or a
        // small window the legend can be larger than the plot, and Math.Clamp
        // throws when min > max, which used to crash inside Render.
        double boxWidth = Math.Min(contentWidth + 12.0, plot.Width);
        double boxHeight = Math.Min(contentHeight + 12.0, plot.Height);
        double boxX = Math.Clamp(hoverCanvasX + 10.0, plot.Left, plot.Right - boxWidth);
        double boxY = Math.Clamp(plot.Top + 6.0, plot.Top, plot.Bottom - boxHeight);
        var box = new Rect(boxX, boxY, boxWidth, boxHeight);
        context.DrawRectangle(LegendBackground, LegendBorderPen, box);

        // Rows that do not fit are clipped to the box instead of spilling over the plot.
        using (context.PushClip(box))
        {
            double rowY = boxY + 6.0;
            foreach (var (text, color) in legendRows)
            {
                if (rowY > box.Bottom) break;
                double swatchY = rowY + Math.Max(0.0, (text.Height - 10.0) / 2.0);
                context.DrawRectangle(GetSwatchBrush(color), SwatchBorderPen,
                    new Rect(boxX + 6.0, swatchY, 10.0, 10.0));
                context.DrawText(text, new Point(boxX + 6.0 + 10.0 + 6.0, rowY));
                rowY += text.Height + 3.0;
            }
        }
    }
}
