using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
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

    private static readonly IReadOnlyDictionary<string, string?> EmptyTags = new Dictionary<string, string?>(0, StringComparer.Ordinal);

    private const int StackThreshold = 128;

    public void Append(DateTimeOffset timestamp, double value, IReadOnlyDictionary<string, string?>? tags)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        var snapshot = tags is null || tags.Count == 0 ? EmptyTags : tags;
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

        var total = _points.Count;
        var finiteCount = 0;
        for (var i = 0; i < total; i++)
        {
            if (double.IsFinite(_points[i].Value))
            {
                finiteCount++;
            }
        }

        if (finiteCount == 0)
        {
            Minimum = 0;
            Maximum = 0;
            Average = 0;
            return;
        }

        double[]? rented = null;
        Span<double> span = finiteCount <= StackThreshold
            ? stackalloc double[StackThreshold]
            : (rented = ArrayPool<double>.Shared.Rent(finiteCount)).AsSpan(0, finiteCount);

        var values = span[..finiteCount];
        var index = 0;
        for (var i = 0; i < total; i++)
        {
            var value = _points[i].Value;
            if (!double.IsFinite(value))
            {
                continue;
            }

            values[index++] = value;
        }

        ComputeStatistics(values, out var min, out var max, out var sum);
        Minimum = min;
        Maximum = max;
        Average = sum / finiteCount;

        if (rented is not null)
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static void ComputeStatistics(ReadOnlySpan<double> values, out double min, out double max, out double sum)
    {
        if (values.IsEmpty)
        {
            min = 0;
            max = 0;
            sum = 0;
            return;
        }

        if (Vector.IsHardwareAccelerated && values.Length >= Vector<double>.Count)
        {
            var lanes = Vector<double>.Count;
            var minVector = new Vector<double>(double.PositiveInfinity);
            var maxVector = new Vector<double>(double.NegativeInfinity);
            var sumVector = Vector<double>.Zero;
            var i = 0;

            for (; i <= values.Length - lanes; i += lanes)
            {
                var vec = new Vector<double>(values.Slice(i, lanes));
                minVector = Vector.Min(minVector, vec);
                maxVector = Vector.Max(maxVector, vec);
                sumVector += vec;
            }

            min = double.PositiveInfinity;
            max = double.NegativeInfinity;
            sum = 0;

            for (var lane = 0; lane < lanes; lane++)
            {
                var laneMin = minVector[lane];
                var laneMax = maxVector[lane];
                if (laneMin < min)
                {
                    min = laneMin;
                }

                if (laneMax > max)
                {
                    max = laneMax;
                }

                sum += sumVector[lane];
            }

            for (; i < values.Length; i++)
            {
                var value = values[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }

                sum += value;
            }

            return;
        }

        min = double.PositiveInfinity;
        max = double.NegativeInfinity;
        sum = 0;

        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }

            sum += value;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? string.Empty));
}

public readonly record struct MetricPoint(DateTimeOffset Timestamp, double Value, IReadOnlyDictionary<string, string?> Tags);

public enum TimelineRenderMode
{
    Line,
    Step
}
