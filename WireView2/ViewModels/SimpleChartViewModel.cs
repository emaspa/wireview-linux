using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace WireView2.ViewModels;

/// <summary>Data model for <see cref="Controls.SimpleLineChart"/>: named series of
/// (X, Y) points with an X window (points behind XMin are trimmed) and a Y range.
/// Ported from the upstream 1.0.7 Windows client.</summary>
public sealed class SimpleChartViewModel : ViewModelBase
{
    public sealed class Series : ViewModelBase
    {
        public string Key { get; }

        public string Name { get; }

        public ObservableCollection<DataPoint> Points { get; } = new();

        public Series(string key, string name)
        {
            Key = key;
            Name = name;
        }

        public void RaiseChanged()
        {
            OnPropertyChanged(nameof(Points));
        }
    }

    public readonly record struct DataPoint(double X, double Y);

    private readonly Dictionary<string, Series> _seriesByKey = new();

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

    public void EnsureSeries(string key, string displayName)
    {
        if (!_seriesByKey.ContainsKey(key))
        {
            _seriesByKey[key] = new Series(key, displayName);
            OnPropertyChanged(nameof(SeriesItems));
        }
    }

    public void AddPoint(string key, double x, double y)
    {
        if (_seriesByKey.TryGetValue(key, out var series))
        {
            series.Points.Add(new DataPoint(x, y));
            double xMin = XMin;
            while (series.Points.Count > 0 && series.Points[0].X < xMin)
                series.Points.RemoveAt(0);
            series.RaiseChanged();
        }
    }

    public void SetXWindow(double xmin, double xmax)
    {
        XMin = xmin;
        XMax = xmax;
        OnPropertyChanged(nameof(XMin));
        OnPropertyChanged(nameof(XMax));
    }

    public void SetYRange(double ymin, double ymax)
    {
        YMin = ymin;
        YMax = ymax;
        OnPropertyChanged(nameof(YMin));
        OnPropertyChanged(nameof(YMax));
    }

    public void AutoScaleY()
    {
        var all = _seriesByKey.Values.SelectMany(s => s.Points).ToList();
        if (all.Count == 0) return;

        double min = all.Min(p => p.Y);
        double max = all.Max(p => p.Y);
        if (Math.Abs(max - min) < 1e-9)
            max = min + 1.0;
        double pad = (max - min) * 0.1;
        SetYRange(min - pad, max + pad);
    }
}
