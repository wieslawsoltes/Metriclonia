using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Metriclonia.Monitor.Metrics;

namespace Metriclonia.Monitor.Visualization;

public sealed class ActivitySeries : INotifyPropertyChanged
{
    private const int MaxRecentEntries = 20;
    private const int MaxDurationHistory = 4096;

    private readonly ObservableCollection<ActivityEntry> _recentEntries = new();
    private readonly List<double> _durationHistory = new();
    private readonly ObservableCollection<ActivityPoint> _points = new();

    private int _totalCount;
    private double _totalDurationMs;
    private double _averageDurationMs;
    private double _minimumDurationMs;
    private double _maximumDurationMs;
    private double _percentile95DurationMs;
    private double _lastDurationMs;
    private bool _hasObservations;
    private DateTimeOffset _lastTimestamp;
    private string _lastStatus = string.Empty;
    private string? _lastStatusDescription;
    private readonly ReadOnlyObservableCollection<ActivityEntry> _readOnlyRecentEntries;
    private readonly ReadOnlyObservableCollection<ActivityPoint> _readOnlyPoints;

    public ActivitySeries(string name, string description, Color color)
    {
        Name = name;
        Description = description;
        Color = color;
        Stroke = new SolidColorBrush(color);
        AccentBrush = new SolidColorBrush(Color.FromArgb(64, color.R, color.G, color.B));
        _readOnlyRecentEntries = new ReadOnlyObservableCollection<ActivityEntry>(_recentEntries);
        _readOnlyPoints = new ReadOnlyObservableCollection<ActivityPoint>(_points);
    }

    public string Name { get; }

    public string DisplayName => Name.Contains('.') ? Name[(Name.LastIndexOf('.') + 1)..] : Name;

    public string Description { get; private set; }

    public Color Color { get; }

    public SolidColorBrush Stroke { get; }

    public SolidColorBrush AccentBrush { get; }

    public ReadOnlyObservableCollection<ActivityEntry> RecentEntries => _readOnlyRecentEntries;

    public ReadOnlyObservableCollection<ActivityPoint> Points => _readOnlyPoints;

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (_totalCount != value)
            {
                _totalCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasObservations));
                OnPropertyChanged(nameof(HasNoObservations));
            }
        }
    }

    public bool HasObservations => TotalCount > 0;

    public bool HasNoObservations => !HasObservations;

    public double AverageDurationMs
    {
        get => _averageDurationMs;
        private set
        {
            if (Math.Abs(_averageDurationMs - value) > 0.0001)
            {
                _averageDurationMs = value;
                OnPropertyChanged();
            }
        }
    }

    public double MinimumDurationMs
    {
        get => _minimumDurationMs;
        private set
        {
            if (Math.Abs(_minimumDurationMs - value) > 0.0001)
            {
                _minimumDurationMs = value;
                OnPropertyChanged();
            }
        }
    }

    public double MaximumDurationMs
    {
        get => _maximumDurationMs;
        private set
        {
            if (Math.Abs(_maximumDurationMs - value) > 0.0001)
            {
                _maximumDurationMs = value;
                OnPropertyChanged();
            }
        }
    }

    public double Percentile95DurationMs
    {
        get => _percentile95DurationMs;
        private set
        {
            if (Math.Abs(_percentile95DurationMs - value) > 0.0001)
            {
                _percentile95DurationMs = value;
                OnPropertyChanged();
            }
        }
    }

    public double LastDurationMs
    {
        get => _lastDurationMs;
        private set
        {
            if (Math.Abs(_lastDurationMs - value) > 0.0001)
            {
                _lastDurationMs = value;
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

    public string LastStatus
    {
        get => _lastStatus;
        private set
        {
            if (!string.Equals(_lastStatus, value, StringComparison.Ordinal))
            {
                _lastStatus = value;
                OnPropertyChanged();
            }
        }
    }

    public string? LastStatusDescription
    {
        get => _lastStatusDescription;
        private set
        {
            if (!string.Equals(_lastStatusDescription, value, StringComparison.Ordinal))
            {
                _lastStatusDescription = value;
                OnPropertyChanged();
            }
        }
    }

    internal void Append(ActivitySample sample)
    {
        var duration = sample.DurationMilliseconds;
        var hadGraphData = HasGraphData;

        TotalCount++;
        _totalDurationMs += duration;
        AverageDurationMs = TotalCount > 0 ? _totalDurationMs / TotalCount : 0;

        if (!_hasObservations)
        {
            MinimumDurationMs = duration;
            MaximumDurationMs = duration;
            _hasObservations = true;
        }
        else
        {
            if (duration < MinimumDurationMs)
            {
                MinimumDurationMs = duration;
            }

            if (duration > MaximumDurationMs)
            {
                MaximumDurationMs = duration;
            }
        }

        LastDurationMs = duration;
        LastTimestamp = sample.StartTimestamp;
        LastStatus = sample.Status;
        LastStatusDescription = sample.StatusDescription;

        AppendDurationHistory(duration);

        var entry = new ActivityEntry(sample.StartTimestamp, duration, sample.Status, sample.StatusDescription, sample.TraceId, sample.SpanId, sample.Tags);
        _recentEntries.Insert(0, entry);

        if (_recentEntries.Count > MaxRecentEntries)
        {
            _recentEntries.RemoveAt(_recentEntries.Count - 1);
        }

        AppendPoint(sample.StartTimestamp, duration, hadGraphData);

        OnPropertyChanged(nameof(RecentEntries));
    }

    internal void TryUpdateDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description) || string.Equals(Description, description, StringComparison.Ordinal))
        {
            return;
        }

        Description = description;
        OnPropertyChanged(nameof(Description));
    }

    private void AppendDurationHistory(double duration)
    {
        _durationHistory.Add(duration);
        if (_durationHistory.Count > MaxDurationHistory)
        {
            _durationHistory.RemoveAt(0);
        }

        if (_durationHistory.Count == 0)
        {
            Percentile95DurationMs = 0;
            return;
        }

        var copy = _durationHistory.ToArray();
        Array.Sort(copy);
        var index = (int)Math.Clamp(Math.Ceiling(copy.Length * 0.95) - 1, 0, copy.Length - 1);
        Percentile95DurationMs = copy[index];
    }

    private void AppendPoint(DateTimeOffset timestamp, double duration, bool hadGraphDataBefore)
    {
        _points.Add(new ActivityPoint(timestamp, duration));
        if (_points.Count > MaxDurationHistory)
        {
            _points.RemoveAt(0);
        }

        if (hadGraphDataBefore != HasGraphData)
        {
            OnPropertyChanged(nameof(HasGraphData));
        }

        OnPropertyChanged(nameof(Points));
    }

    public bool HasGraphData => _points.Count > 0;

    internal void TrimBefore(DateTimeOffset cutoff)
    {
        var removedPoints = false;
        while (_points.Count > 0 && _points[0].Timestamp < cutoff)
        {
            _points.RemoveAt(0);
            removedPoints = true;
        }

        if (removedPoints)
        {
            OnPropertyChanged(nameof(Points));
            OnPropertyChanged(nameof(HasGraphData));
        }

        var removedEntries = false;
        for (var i = _recentEntries.Count - 1; i >= 0; i--)
        {
            if (_recentEntries[i].StartTimestamp < cutoff)
            {
                _recentEntries.RemoveAt(i);
                removedEntries = true;
            }
        }

        if (removedEntries)
        {
            OnPropertyChanged(nameof(RecentEntries));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? string.Empty));
}

public sealed class ActivityEntry
{
    private readonly IReadOnlyDictionary<string, string?> _tags;
    private string? _tagSummary;

    public ActivityEntry(DateTimeOffset startTimestamp, double durationMilliseconds, string status, string? statusDescription, string traceId, string spanId, IDictionary<string, string?>? tags)
    {
        StartTimestamp = startTimestamp;
        DurationMilliseconds = durationMilliseconds;
        Status = status;
        StatusDescription = statusDescription;
        TraceId = traceId;
        SpanId = spanId;
        _tags = tags is null || tags.Count == 0
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(tags, StringComparer.Ordinal);
    }

    public DateTimeOffset StartTimestamp { get; }

    public DateTimeOffset EndTimestamp => StartTimestamp + TimeSpan.FromMilliseconds(DurationMilliseconds);

    public double DurationMilliseconds { get; }

    public string Status { get; }

    public string? StatusDescription { get; }

    public string TraceId { get; }

    public string SpanId { get; }

    public IReadOnlyDictionary<string, string?> Tags => _tags;

    public string TagSummary => _tagSummary ??= BuildTagSummary();

    private string BuildTagSummary()
    {
        if (_tags.Count == 0)
        {
            return "No tags";
        }

        var parts = _tags.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(kvp => string.IsNullOrEmpty(kvp.Value) ? kvp.Key : $"{kvp.Key}={kvp.Value}");
        return string.Join(", ", parts);
    }
}

public readonly record struct ActivityPoint(DateTimeOffset Timestamp, double DurationMilliseconds);
