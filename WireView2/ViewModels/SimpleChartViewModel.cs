using System;
using System.Collections.Generic;

namespace WireView2.ViewModels;

/// <summary>Data model for <see cref="Controls.SimpleLineChart"/>: named series of
/// (X, Y) points with an X window and a Y range. Ported from the upstream 1.0.7
/// Windows client; since 1.0.8 a series holds an immutable snapshot replaced with
/// <see cref="Series.SetPoints"/> (one change notification, one chart repaint)
/// instead of an observable collection that notified per added/removed point.</summary>
public sealed class SimpleChartViewModel : ViewModelBase
{
    public sealed class Series : ViewModelBase
    {
        private IReadOnlyList<DataPoint> _points = Array.Empty<DataPoint>();

        public string Key { get; }

        public string Name { get; }

        /// <summary>Points in ascending X order. Treat as read-only: the chart may
        /// render it at any time, so replace it with <see cref="SetPoints"/>.</summary>
        public IReadOnlyList<DataPoint> Points => _points;

        public Series(string key, string name)
        {
            Key = key;
            Name = name;
        }

        public void SetPoints(IReadOnlyList<DataPoint> points)
        {
            _points = points;
            OnPropertyChanged(nameof(Points));
        }
    }

    public readonly record struct DataPoint(double X, double Y);

    private readonly Dictionary<string, Series> _seriesByKey = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Series> SeriesItems => _seriesByKey.Values;

    public double XMin { get; private set; }

    public double XMax { get; private set; }

    public double YMin { get; private set; }

    public double YMax { get; private set; } = 100.0;

    public void ClearSeries()
    {
        _seriesByKey.Clear();
        OnPropertyChanged(nameof(SeriesItems));
    }

    public Series EnsureSeries(string key, string displayName)
    {
        if (_seriesByKey.TryGetValue(key, out var existing))
            return existing;
        var series = new Series(key, displayName);
        _seriesByKey[key] = series;
        OnPropertyChanged(nameof(SeriesItems));
        return series;
    }

    public Series? GetSeries(string key) =>
        _seriesByKey.TryGetValue(key, out var series) ? series : null;

    public void SetXWindow(double xmin, double xmax)
    {
        if (XMin == xmin && XMax == xmax) return;
        XMin = xmin;
        XMax = xmax;
        OnPropertyChanged(nameof(XMin));
        OnPropertyChanged(nameof(XMax));
    }

    public void SetYRange(double ymin, double ymax)
    {
        if (YMin == ymin && YMax == ymax) return;
        YMin = ymin;
        YMax = ymax;
        OnPropertyChanged(nameof(YMin));
        OnPropertyChanged(nameof(YMax));
    }
}
