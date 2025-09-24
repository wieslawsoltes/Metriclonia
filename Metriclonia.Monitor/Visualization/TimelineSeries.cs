using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;

namespace Metriclonia.Monitor.Visualization;

public sealed class TimelineSeries : INotifyPropertyChanged
{
    private readonly ObservableCollection<MetricPoint> _points = new();
    private double _latestValue;
    private double _minimum;
    private double _maximum;
    private double _average;
    private bool _isVisible = true;
    private DateTimeOffset _lastTimestamp;

    public TimelineSeries(string meterName, string instrumentName, string instrumentType, string displayName, string unit, string description, string tagSignature, TimelineRenderMode renderMode, Color color)
    {
        MeterName = meterName;
        InstrumentName = instrumentName;
        InstrumentType = instrumentType;
        DisplayName = displayName;
        Unit = unit;
        Description = description;
        TagSignature = tagSignature;
        RenderMode = renderMode;
        Color = color;
        Stroke = new SolidColorBrush(color);
        Fill = new SolidColorBrush(Color.FromArgb(64, color.R, color.G, color.B));
        Points = new ReadOnlyObservableCollection<MetricPoint>(_points);
    }

    public string MeterName { get; }

    public string InstrumentName { get; }

    public string InstrumentType { get; }

    public string DisplayName { get; }

    public string Unit { get; }

    public string Description { get; }

    public string TagSignature { get; }

    public TimelineRenderMode RenderMode { get; }

    public Color Color { get; }

    public SolidColorBrush Stroke { get; }

    public SolidColorBrush Fill { get; }

    public ReadOnlyObservableCollection<MetricPoint> Points { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                OnPropertyChanged();
            }
        }
    }

    public double LatestValue
    {
        get => _latestValue;
        private set
        {
            if (Math.Abs(value - _latestValue) > 0.001)
            {
                _latestValue = value;
                OnPropertyChanged();
            }
        }
    }

    public double Minimum
    {
        get => _minimum;
        private set
        {
            if (Math.Abs(value - _minimum) > 0.001)
            {
                _minimum = value;
                OnPropertyChanged();
            }
        }
    }

    public double Maximum
    {
        get => _maximum;
        private set
        {
            if (Math.Abs(value - _maximum) > 0.001)
            {
                _maximum = value;
                OnPropertyChanged();
            }
        }
    }

    public double Average
    {
        get => _average;
        private set
        {
            if (Math.Abs(value - _average) > 0.001)
            {
                _average = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTimeOffset LastTimestamp
    {
        get => _lastTimestamp;
        private set
        {
            if (value != _lastTimestamp)
            {
                _lastTimestamp = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event NotifyCollectionChangedEventHandler? PointsChanged;

    public void Append(DateTimeOffset timestamp, double value, IDictionary<string, string?>? tags)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        IDictionary<string, string?> snapshot;
        if (tags is null || tags.Count == 0)
        {
            snapshot = new Dictionary<string, string?>();
        }
        else
        {
            snapshot = new Dictionary<string, string?>(tags);
        }

        var point = new MetricPoint(timestamp, value, snapshot);
        _points.Add(point);
        LatestValue = value;
        LastTimestamp = timestamp;
        RecalculateStats();
        PointsChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, point));
    }

    public void TrimBefore(DateTimeOffset cutoff)
    {
        var removed = false;
        while (_points.Count > 0 && _points[0].Timestamp < cutoff)
        {
            _points.RemoveAt(0);
            removed = true;
        }

        if (removed)
        {
            RecalculateStats();
            PointsChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    private void RecalculateStats()
    {
        if (_points.Count == 0)
        {
            Minimum = 0;
            Maximum = 0;
            Average = 0;
            return;
        }

        var finite = _points.Where(static p => double.IsFinite(p.Value)).ToList();
        if (finite.Count == 0)
        {
            Minimum = 0;
            Maximum = 0;
            Average = 0;
            return;
        }

        Minimum = finite.Min(p => p.Value);
        Maximum = finite.Max(p => p.Value);
        Average = finite.Average(p => p.Value);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? string.Empty));
}

public readonly record struct MetricPoint(DateTimeOffset Timestamp, double Value, IDictionary<string, string?> Tags);

public enum TimelineRenderMode
{
    Line,
    Step
}
