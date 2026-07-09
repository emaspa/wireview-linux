using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;

namespace WireView2.Controls;

/// <summary>Grouped vertical bar chart drawn directly with DrawingContext —
/// replaces the LiveCharts CartesianChart for the Overview per-wire bars (ported
/// from the upstream 1.0.7 Windows client). Still consumes LiveCharts data types
/// (ColumnSeries&lt;double&gt; / Axis) as its input contract. Bars get a
/// DeepSkyBlue→Red gradient scaled by each value's position in the Y range.</summary>
public sealed class SimpleBarChart : Control
{
    private const double MinBarHeightPx = 3.0;
    private const int SameSnapshotUpdateInterval = 100;

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<SimpleBarChart, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SimpleBarChart, string?>(nameof(Label));

    public static readonly StyledProperty<IReadOnlyList<ISeries>?> SeriesProperty =
        AvaloniaProperty.Register<SimpleBarChart, IReadOnlyList<ISeries>?>(nameof(Series));

    public static readonly StyledProperty<Axis[]?> XAxesProperty =
        AvaloniaProperty.Register<SimpleBarChart, Axis[]?>(nameof(XAxes));

    public static readonly StyledProperty<Axis[]?> YAxesProperty =
        AvaloniaProperty.Register<SimpleBarChart, Axis[]?>(nameof(YAxes));

    public static readonly StyledProperty<bool> ShowBarValuesProperty =
        AvaloniaProperty.Register<SimpleBarChart, bool>(nameof(ShowBarValues));

    private IReadOnlyList<ISeries>? _subscribedSeries;
    private readonly Dictionary<INotifyCollectionChanged, NotifyCollectionChangedEventHandler> _collectionHandlers = new();
    private readonly Dictionary<INotifyPropertyChanged, PropertyChangedEventHandler> _propertyHandlers = new();

    private string? _lastRenderedSnapshot;
    private int _sameSnapshotUpdateCounter;

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private IBrush TextBrush => Foreground ?? Brushes.LightGray;

    public IReadOnlyList<ISeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public Axis[]? XAxes
    {
        get => GetValue(XAxesProperty);
        set => SetValue(XAxesProperty, value);
    }

    public Axis[]? YAxes
    {
        get => GetValue(YAxesProperty);
        set => SetValue(YAxesProperty, value);
    }

    /// <summary>Draw each bar's value under that bar, tinted in the series color.
    /// The unit is taken from a "(X)" suffix in the series name, e.g. "Current (A)".</summary>
    public bool ShowBarValues
    {
        get => GetValue(ShowBarValuesProperty);
        set => SetValue(ShowBarValuesProperty, value);
    }

    static SimpleBarChart()
    {
        AffectsRender<SimpleBarChart>(LabelProperty, SeriesProperty, XAxesProperty,
            YAxesProperty, ForegroundProperty, ShowBarValuesProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SeriesProperty)
            SubscribeToSeries(change.NewValue as IReadOnlyList<ISeries>);
        if (change.Property == XAxesProperty || change.Property == YAxesProperty)
        {
            SubscribeToAxes(XAxes, YAxes);
            InvalidateOnUiThread();
        }
    }

    private void SubscribeToSeries(IReadOnlyList<ISeries>? series)
    {
        if (_subscribedSeries == series) return;
        UnsubscribeAll();
        _subscribedSeries = series;
        if (series == null) return;

        foreach (var s in series)
        {
            if (s is not ColumnSeries<double> column) continue;

            if (column.Values is INotifyCollectionChanged incc)
            {
                NotifyCollectionChangedEventHandler handler = delegate { InvalidateOnUiThread(); };
                _collectionHandlers[incc] = handler;
                incc.CollectionChanged += handler;
            }
            INotifyPropertyChanged inpc = column;
            PropertyChangedEventHandler pHandler = delegate { InvalidateOnUiThread(); };
            _propertyHandlers[inpc] = pHandler;
            inpc.PropertyChanged += pHandler;
        }
        SubscribeToAxes(XAxes, YAxes);
        InvalidateOnUiThread();
    }

    private void SubscribeToAxes(Axis[]? xAxes, Axis[]? yAxes)
    {
        foreach (var axis in (xAxes ?? Array.Empty<Axis>()).Concat(yAxes ?? Array.Empty<Axis>()))
        {
            INotifyPropertyChanged? inpc = axis;
            if (inpc != null && !_propertyHandlers.ContainsKey(inpc))
            {
                PropertyChangedEventHandler handler = delegate { InvalidateOnUiThread(); };
                _propertyHandlers[inpc] = handler;
                inpc.PropertyChanged += handler;
            }
        }
    }

    private void UnsubscribeAll()
    {
        foreach (var (incc, handler) in _collectionHandlers.ToArray())
        {
            incc.CollectionChanged -= handler;
            _collectionHandlers.Remove(incc);
        }
        foreach (var (inpc, handler) in _propertyHandlers.ToArray())
        {
            inpc.PropertyChanged -= handler;
            _propertyHandlers.Remove(inpc);
        }
    }

    private void InvalidateOnUiThread()
    {
        if (Dispatcher.UIThread.CheckAccess())
            InvalidateIfValuesChangedOrNthRepeat();
        else
            Dispatcher.UIThread.Post(InvalidateIfValuesChangedOrNthRepeat);
    }

    // Live telemetry pushes identical value sets every poll — skip those repaints
    // (with an every-100th safety net) by comparing a serialized snapshot.
    private void InvalidateIfValuesChangedOrNthRepeat()
    {
        string? snapshot = TryBuildSnapshotString();
        if (snapshot == null)
        {
            InvalidateVisual();
            return;
        }
        if (string.Equals(snapshot, _lastRenderedSnapshot, StringComparison.Ordinal))
        {
            _sameSnapshotUpdateCounter++;
            if (_sameSnapshotUpdateCounter % SameSnapshotUpdateInterval != 0)
                return;
        }
        else
        {
            _sameSnapshotUpdateCounter = 0;
        }
        InvalidateVisual();
    }

    private string? TryBuildSnapshotString()
    {
        var series = Series;
        if (series == null || series.Count == 0) return null;

        var visible = series.OfType<ColumnSeries<double>>().Where(s => s.IsVisible).ToList();
        if (visible.Count == 0) return null;

        IEnumerable<double>? first = visible.Select(c => c.Values?.Cast<double>())
            .FirstOrDefault(v => v != null);
        if (first == null) return null;

        int count = first.Count();
        if (count <= 0) return null;

        var sb = new StringBuilder(visible.Count * count * 6);
        foreach (var column in visible)
        {
            IEnumerable<double>? values = column.Values?.Cast<double>();
            if (values == null) continue;
            for (int i = 0; i < count; i++)
            {
                sb.Append(values.ElementAtOrDefault(i).ToString("R", CultureInfo.InvariantCulture));
                sb.Append('|');
            }
            sb.Append('#');
        }
        return sb.ToString();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _lastRenderedSnapshot = TryBuildSnapshotString();

        var bounds = new Rect(Bounds.Size);

        if (!string.IsNullOrWhiteSpace(Label))
        {
            var title = new FormattedText(Label, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 18.0, TextBrush)
            {
                TextAlignment = TextAlignment.Center,
            };
            context.DrawText(title, new Point(bounds.Center.X - title.Width / 2.0, 5.0));
        }

        var series = Series;
        if (series == null || series.Count == 0) return;

        var visible = series.OfType<ColumnSeries<double>>().Where(s => s.IsVisible).ToList();
        if (visible.Count == 0) return;

        IEnumerable<double>? firstValues = visible.Select(c => c.Values?.Cast<double>())
            .FirstOrDefault(v => v != null);
        int valueCount = firstValues?.Count() ?? 0;
        if (valueCount <= 0) return;

        IList<string>? xLabels = XAxes?.FirstOrDefault()?.Labels;
        Axis[] yAxes = YAxes ?? Array.Empty<Axis>();

        // Labels may be multi-line (one measure per visible series) — size the
        // bottom band to the tallest one so lines never overlap the bars.
        var labelTexts = new FormattedText?[valueCount];
        double maxLabelHeight = 0.0;
        if (xLabels != null)
        {
            for (int i = 0; i < valueCount && i < xLabels.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(xLabels[i])) continue;
                labelTexts[i] = new FormattedText(xLabels[i], CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 13.0, TextBrush)
                {
                    TextAlignment = TextAlignment.Center,
                };
                maxLabelHeight = Math.Max(maxLabelHeight, labelTexts[i]!.Height);
            }
        }
        double bottomPad = Math.Max(28.0, maxLabelHeight + 12.0);
        if (ShowBarValues)
            bottomPad = Math.Max(bottomPad, 22.0);

        var plot = new Rect(bounds.X + 40.0, bounds.Y + 38.0,
            Math.Max(0.0, bounds.Width - 40.0 - 10.0),
            Math.Max(0.0, bounds.Height - 38.0 - bottomPad));

        // Auto Y range per axis from the data, used where the axis has no limits.
        var axisDataMin = new double[Math.Max(1, yAxes.Length)];
        var axisDataMax = new double[Math.Max(1, yAxes.Length)];
        for (int i = 0; i < axisDataMin.Length; i++)
        {
            axisDataMin[i] = double.PositiveInfinity;
            axisDataMax[i] = double.NegativeInfinity;
        }
        foreach (var column in visible)
        {
            int axisIndex = Math.Clamp(column.ScalesYAt, 0, Math.Max(0, axisDataMin.Length - 1));
            IEnumerable<double>? values = column.Values?.Cast<double>();
            if (values == null) continue;
            foreach (double v in values.Take(valueCount))
            {
                if (!double.IsFinite(v)) continue;
                if (v < axisDataMin[axisIndex]) axisDataMin[axisIndex] = v;
                if (v > axisDataMax[axisIndex]) axisDataMax[axisIndex] = v;
            }
        }

        double groupWidth = plot.Width / valueCount;
        double groupGap = Math.Max(1.0, groupWidth * 0.15);
        double barWidth = Math.Max(1.0, groupWidth - groupGap) / visible.Count;

        for (int gi = 0; gi < valueCount; gi++)
        {
            for (int si = 0; si < visible.Count; si++)
            {
                var column = visible[si];
                int axisIndex = Math.Clamp(column.ScalesYAt, 0, Math.Max(0, yAxes.Length - 1));
                double yMin = GetYMin(axisIndex);
                double yMax = GetYMax(axisIndex);
                if (yMax <= yMin) yMax = yMin + 1.0;

                IEnumerable<double>? values = column.Values?.Cast<double>();
                if (values == null) continue;

                double value = values.ElementAtOrDefault(gi);
                double left = plot.Left + gi * groupWidth + groupGap / 2.0 + si * barWidth;
                double right = left + Math.Max(1.0, barWidth - 1.0);

                double ratio = (value - yMin) / (yMax - yMin);
                ratio = double.IsFinite(ratio) ? Math.Clamp(ratio, 0.0, 1.0) : 0.0;
                double top = plot.Bottom - ratio * plot.Height;
                double minTop = Math.Max(plot.Top, plot.Bottom - MinBarHeightPx);
                if (top > minTop) top = minTop;

                var barRect = new Rect(new Point(left, top), new Point(right, plot.Bottom));
                // A SolidColorPaint fill on the series overrides the default base
                // color so multi-series charts stay distinguishable.
                Color baseColor = column.Fill is SolidColorPaint paint
                    ? Color.FromArgb(paint.Color.Alpha, paint.Color.Red, paint.Color.Green, paint.Color.Blue)
                    : Colors.DeepSkyBlue;
                var tipColor = Lerp(baseColor, Colors.Red, ratio);
                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.0, 1.0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.0, 0.0, RelativeUnit.Relative),
                    Opacity = 0.9,
                    GradientStops = new GradientStops
                    {
                        new GradientStop(baseColor, 0.0),
                        new GradientStop(tipColor, 1.0),
                    },
                };
                double cornerRadius = Math.Max(0.0, Math.Min(6.0, Math.Min(barRect.Width, barRect.Height) / 2.0));
                context.DrawRectangle(brush, null, barRect, cornerRadius, cornerRadius);

                if (ShowBarValues && double.IsFinite(value))
                {
                    string unit = ExtractUnit(column.Name);
                    string text = unit.Length > 0 ? $"{value:0.0} {unit}" : $"{value:0.0}";
                    var valueLabel = new FormattedText(text, CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, Typeface.Default, 11.0,
                        new SolidColorBrush(baseColor))
                    {
                        TextAlignment = TextAlignment.Center,
                    };
                    double barCenter = (left + right) / 2.0;
                    context.DrawText(valueLabel,
                        new Point(barCenter - valueLabel.Width / 2.0, plot.Bottom + 5.0));
                }
            }

            if (labelTexts[gi] is { } label)
            {
                double lx = plot.Left + gi * groupWidth + groupWidth / 2.0 - label.Width / 2.0;
                double ly = plot.Bottom + 6.0;
                context.DrawText(label, new Point(lx, ly));
            }
        }

        double GetYMax(int idx)
        {
            if (idx >= 0 && idx < yAxes.Length && yAxes[idx].MaxLimit is { } max)
                return max;
            if (idx >= 0 && idx < axisDataMax.Length && double.IsFinite(axisDataMax[idx]))
                return Math.Max(1.0, axisDataMax[idx]);
            return 1.0;
        }

        double GetYMin(int idx)
        {
            if (idx >= 0 && idx < yAxes.Length && yAxes[idx].MinLimit is { } min)
                return min;
            if (idx >= 0 && idx < axisDataMin.Length && double.IsFinite(axisDataMin[idx]))
                return Math.Min(0.0, axisDataMin[idx]);
            return 0.0;
        }

        static Color Lerp(Color a, Color b, double t)
        {
            t = double.IsFinite(t) ? Math.Clamp(t, 0.0, 1.0) : 0.0;
            return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
            byte L(byte from, byte to) =>
                (byte)Math.Clamp((int)Math.Round(from + (to - from) * t), 0, 255);
        }

        static string ExtractUnit(string? seriesName)
        {
            if (string.IsNullOrEmpty(seriesName)) return string.Empty;
            int open = seriesName.LastIndexOf('(');
            int close = seriesName.LastIndexOf(')');
            return open >= 0 && close > open + 1
                ? seriesName[(open + 1)..close]
                : string.Empty;
        }
    }
}
