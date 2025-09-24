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
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Metriclonia.Monitor.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Metriclonia.Monitor.Visualization;

public class TimelinePlotControl : Control
{
    private static readonly ILogger Logger = Log.For<TimelinePlotControl>();
    private const bool EnableRenderTraceLogging = false;

    public static readonly StyledProperty<IEnumerable<TimelineSeries>?> SeriesProperty =
        AvaloniaProperty.Register<TimelinePlotControl, IEnumerable<TimelineSeries>?>(nameof(Series));

    public static readonly StyledProperty<double> VisibleDurationSecondsProperty =
        AvaloniaProperty.Register<TimelinePlotControl, double>(nameof(VisibleDurationSeconds), 30d);

    public static readonly StyledProperty<TriggerConfiguration?> TriggerConfigurationProperty =
        AvaloniaProperty.Register<TimelinePlotControl, TriggerConfiguration?>(nameof(TriggerConfiguration));

    private readonly HashSet<TimelineSeries> _attachedSeries = new();
    private readonly DispatcherTimer _clockTimer;
    private TriggerConfiguration? _currentTriggerConfiguration;
    private DateTimeOffset _lastTriggerTimestamp;
    private bool _isHoldActive;

    static TimelinePlotControl()
    {
        AffectsRender<TimelinePlotControl>(SeriesProperty, VisibleDurationSecondsProperty, TriggerConfigurationProperty);
    }

    public TimelinePlotControl()
    {
        _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => InvalidateVisual());
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public IEnumerable<TimelineSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double VisibleDurationSeconds
    {
        get => GetValue(VisibleDurationSecondsProperty);
        set => SetValue(VisibleDurationSecondsProperty, value);
    }

    public TriggerConfiguration? TriggerConfiguration
    {
        get => GetValue(TriggerConfigurationProperty);
        set => SetValue(TriggerConfigurationProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SeriesProperty)
        {
            if (change.OldValue is IEnumerable<TimelineSeries> oldSeries)
            {
                DetachSeries(oldSeries);
            }

            if (change.NewValue is IEnumerable<TimelineSeries> newSeries)
            {
                AttachSeries(newSeries);
            }

            InvalidateVisual();
        }
        else if (change.Property == VisibleDurationSecondsProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == TriggerConfigurationProperty)
        {
            UpdateTriggerConfiguration(change.OldValue as TriggerConfiguration, change.NewValue as TriggerConfiguration);
            InvalidateVisual();
        }
    }

    private void UpdateTriggerConfiguration(TriggerConfiguration? oldConfig, TriggerConfiguration? newConfig)
    {
        if (ReferenceEquals(oldConfig, newConfig))
        {
            return;
        }

        if (oldConfig is not null)
        {
            oldConfig.PropertyChanged -= OnTriggerConfigurationChanged;
        }

        _currentTriggerConfiguration = newConfig;
        ResetTriggerState();

        if (newConfig is not null)
        {
            newConfig.PropertyChanged += OnTriggerConfigurationChanged;
        }
    }

    private void ResetTriggerState()
    {
        _lastTriggerTimestamp = default;
        _isHoldActive = false;

        if (_currentTriggerConfiguration is { })
        {
            _currentTriggerConfiguration.LastResolvedEvent = default;
        }
    }

    private void ReleaseTriggerHold()
    {
        if (_isHoldActive)
        {
            _isHoldActive = false;
        }
    }

    private void OnTriggerConfigurationChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TriggerConfiguration.TargetSeries):
                ResetTriggerState();
                break;
            case nameof(TriggerConfiguration.IsEnabled):
                if (_currentTriggerConfiguration is { IsEnabled: true })
                {
                    ReleaseTriggerHold();
                }
                else
                {
                    ResetTriggerState();
                }

                break;
            case nameof(TriggerConfiguration.FreezeOnTrigger):
                if (_currentTriggerConfiguration is { FreezeOnTrigger: false })
                {
                    ReleaseTriggerHold();
                }

                break;
        }

        InvalidateVisual();
    }

    private void AttachSeries(IEnumerable<TimelineSeries> series)
    {
        if (series is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += OnSeriesCollectionChanged;
        }

        foreach (var item in series)
        {
            Subscribe(item);
        }
    }

    private void DetachSeries(IEnumerable<TimelineSeries> series)
    {
        if (series is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged -= OnSeriesCollectionChanged;
        }

        foreach (var item in series)
        {
            Unsubscribe(item);
        }
    }

    private void OnSeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is { Count: > 0 })
        {
            foreach (var item in e.OldItems.OfType<TimelineSeries>())
            {
                Unsubscribe(item);
            }
        }

        if (e.NewItems is { Count: > 0 })
        {
            foreach (var item in e.NewItems.OfType<TimelineSeries>())
            {
                Subscribe(item);
            }
        }

        InvalidateVisual();
    }

    private void Subscribe(TimelineSeries series)
    {
        if (!_attachedSeries.Add(series))
        {
            return;
        }

        series.PropertyChanged += OnSeriesPropertyChanged;
        series.PointsChanged += OnSeriesPointsChanged;
    }

    private void Unsubscribe(TimelineSeries series)
    {
        if (_attachedSeries.Remove(series))
        {
            series.PropertyChanged -= OnSeriesPropertyChanged;
            series.PointsChanged -= OnSeriesPointsChanged;
        }
    }

    private void OnSeriesPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => InvalidateVisual();

    private void OnSeriesPointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_clockTimer.IsEnabled)
        {
            _clockTimer.Start();
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_clockTimer.IsEnabled)
        {
            _clockTimer.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (EnableRenderTraceLogging)
        {
            Logger.LogTrace("Render pass bounds={Bounds}", Bounds);
        }
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(240, 12, 16, 24)), bounds);

        var allSeries = Series?.ToList();
        if (allSeries is null || allSeries.Count == 0)
        {
            DrawEmptyState(context, bounds);
            return;
        }

        var seriesList = allSeries.Where(static s => s.IsVisible).ToList();
        if (seriesList.Count == 0)
        {
            DrawEmptyState(context, bounds);
            return;
        }

        var labelWidth = 150;
        var horizontalPadding = 16;
        var topPadding = 16;
        var bottomPadding = 36;

        var plotWidth = Math.Max(10, bounds.Width - labelWidth - horizontalPadding * 2);
        var plotHeight = Math.Max(10, bounds.Height - topPadding - bottomPadding);
        var laneHeight = plotHeight / seriesList.Count;

        var visibleDuration = Math.Max(1e-9, VisibleDurationSeconds);
        var latestSample = allSeries.Max(static s => s.LastTimestamp);
        var now = DateTimeOffset.UtcNow;
        var fallbackLatest = latestSample != default ? latestSample : now;
        var window = ComputeWindow(allSeries, fallbackLatest, visibleDuration);
        var startTime = window.Start;
        var endTime = window.End;

        DrawTimeGrid(context, labelWidth, topPadding, plotWidth, plotHeight, startTime, endTime);

        for (var index = 0; index < seriesList.Count; index++)
        {
            var series = seriesList[index];
            var laneTop = topPadding + index * laneHeight;
            var laneRect = new Rect(labelWidth, laneTop, plotWidth, laneHeight);
            DrawLaneBackground(context, laneRect, index);
            DrawSeriesLabel(context, series, new Rect(0, laneTop, labelWidth - 8, laneHeight));
            DrawSeries(context, series, laneRect, startTime, endTime);
        }

        if (window.IsTriggered)
        {
            DrawTriggerMarker(context, labelWidth, topPadding, plotWidth, plotHeight, startTime, endTime, window.Anchor);
        }

        DrawTimeAxis(context, labelWidth, topPadding, plotWidth, plotHeight, startTime, endTime, window.IsTriggered, window.Anchor);
    }

    private (DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Anchor, bool IsTriggered) ComputeWindow(IReadOnlyList<TimelineSeries> allSeries, DateTimeOffset fallbackLatest, double visibleDurationSeconds)
    {
        var trigger = _currentTriggerConfiguration;
        var anchor = fallbackLatest;
        var isTriggered = false;

        if (trigger is not null && trigger.IsEnabled)
        {
            var freezeEnabled = trigger.FreezeOnTrigger;

            if (freezeEnabled && _isHoldActive && _lastTriggerTimestamp != default)
            {
                anchor = _lastTriggerTimestamp;
                isTriggered = true;
            }
            else if (TryFindTriggerTimestamp(allSeries, trigger, visibleDurationSeconds, out var candidate, out var isFallback))
            {
                if (!isFallback && _lastTriggerTimestamp != default && trigger.HoldoffSeconds > 0)
                {
                    var deltaSeconds = Math.Abs((candidate - _lastTriggerTimestamp).TotalSeconds);
                    if (deltaSeconds < trigger.HoldoffSeconds)
                    {
                        candidate = _lastTriggerTimestamp;
                    }
                }

                anchor = candidate != default ? candidate : fallbackLatest;
                _lastTriggerTimestamp = anchor;

                if (anchor != default)
                {
                    trigger.LastResolvedEvent = anchor;
                }

                isTriggered = anchor != default;
                _isHoldActive = freezeEnabled && !isFallback && isTriggered;
            }
            else if (trigger.Mode == TriggerMode.Normal && _lastTriggerTimestamp != default)
            {
                anchor = _lastTriggerTimestamp;
                isTriggered = true;
                _isHoldActive = freezeEnabled && _isHoldActive;
            }
            else
            {
                anchor = fallbackLatest;
                _isHoldActive = false;
            }
        }
        else if (_isHoldActive || _lastTriggerTimestamp != default)
        {
            ResetTriggerState();
        }

        if (anchor == default)
        {
            anchor = fallbackLatest;
        }

        if (!isTriggered)
        {
            var end = anchor;
            var start = end - TimeSpan.FromSeconds(visibleDurationSeconds);
            return (start, end, anchor, false);
        }

        var position = trigger?.HorizontalPosition ?? 0.8;
        if (!double.IsFinite(position))
        {
            position = 0.8;
        }
        position = Math.Clamp(position, 0, 1);

        var preSeconds = visibleDurationSeconds * position;
        var startTime = anchor - TimeSpan.FromSeconds(preSeconds);
        var endTime = startTime + TimeSpan.FromSeconds(visibleDurationSeconds);

        if (endTime <= startTime)
        {
            endTime = startTime + TimeSpan.FromSeconds(visibleDurationSeconds);
        }

        return (startTime, endTime, anchor, true);
    }

    private bool TryFindTriggerTimestamp(IReadOnlyList<TimelineSeries> allSeries, TriggerConfiguration trigger, double visibleDurationSeconds, out DateTimeOffset timestamp, out bool isFallback)
    {
        timestamp = default;
        isFallback = false;

        if (allSeries.Count == 0)
        {
            return false;
        }

        var targetSeries = trigger.TargetSeries;
        if (targetSeries is null || !allSeries.Contains(targetSeries))
        {
            targetSeries = allSeries[0];
        }

        if (targetSeries is null)
        {
            return false;
        }

        var points = ExtractRecentPoints(targetSeries, visibleDurationSeconds);
        if (points.Count < 2)
        {
            if (points.Count > 0 && trigger.Mode == TriggerMode.Auto)
            {
                var last = points[^1];
                if (last.Timestamp != default && double.IsFinite(last.Value))
                {
                    timestamp = last.Timestamp;
                    isFallback = true;
                    return true;
                }
            }

            return false;
        }

        var detected = trigger.Type switch
        {
            TriggerType.Edge => TryDetectEdge(points, trigger, out timestamp),
            TriggerType.Pulse => TryDetectPulse(points, trigger, out timestamp),
            TriggerType.Video => TryDetectVideo(points, trigger, out timestamp),
            TriggerType.Logic => TryDetectLogic(points, trigger, allSeries, out timestamp),
            TriggerType.Runt => TryDetectRunt(points, trigger, out timestamp),
            TriggerType.Window => TryDetectWindow(points, trigger, out timestamp),
            TriggerType.Pattern => TryDetectPattern(points, trigger, out timestamp),
            TriggerType.Serial => TryDetectSerial(points, trigger, out timestamp),
            TriggerType.Visual => TryDetectVisual(points, trigger, out timestamp),
            _ => false
        };

        if (!detected && trigger.Mode == TriggerMode.Auto)
        {
            var last = points[^1];
            if (last.Timestamp != default && double.IsFinite(last.Value))
            {
                timestamp = last.Timestamp;
                isFallback = true;
                return true;
            }
        }

        return detected;
    }

    private static List<MetricPoint> ExtractRecentPoints(TimelineSeries targetSeries, double visibleDurationSeconds)
    {
        var result = new List<MetricPoint>();
        var points = targetSeries.Points;
        if (points.Count == 0)
        {
            return result;
        }

        var lookbackSeconds = double.IsFinite(visibleDurationSeconds)
            ? Math.Max(visibleDurationSeconds * 2, visibleDurationSeconds + 5)
            : 10;

        if (lookbackSeconds <= 0)
        {
            lookbackSeconds = visibleDurationSeconds > 0 ? visibleDurationSeconds : 10;
        }

        var reference = targetSeries.LastTimestamp != default ? targetSeries.LastTimestamp : DateTimeOffset.UtcNow;
        var minTimestamp = reference - TimeSpan.FromSeconds(lookbackSeconds);

        foreach (var point in points)
        {
            if (point.Timestamp <= DateTimeOffset.MinValue)
            {
                continue;
            }

            if (!double.IsFinite(point.Value))
            {
                continue;
            }

            if (point.Timestamp >= minTimestamp)
            {
                result.Add(point);
            }
        }

        if (result.Count < 2)
        {
            result = points.Where(static p => p.Timestamp > DateTimeOffset.MinValue && double.IsFinite(p.Value)).ToList();
        }

        return result;
    }

    private static double ResolveLevel(IList<MetricPoint> points, TriggerConfiguration trigger)
    {
        if (!trigger.AutoLevel)
        {
            return trigger.Level;
        }

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;

        foreach (var point in points)
        {
            if (!double.IsFinite(point.Value))
            {
                continue;
            }

            if (point.Value < min)
            {
                min = point.Value;
            }

            if (point.Value > max)
            {
                max = point.Value;
            }
        }

        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return trigger.Level;
        }

        if (Math.Abs(max - min) < double.Epsilon)
        {
            return trigger.Level != 0 ? trigger.Level : max;
        }

        return min + (max - min) / 2.0;
    }

    private static bool TryDetectEdge(IList<MetricPoint> points, TriggerConfiguration trigger, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var level = ResolveLevel(points, trigger);

        for (var i = points.Count - 1; i >= 1; i--)
        {
            var current = points[i];
            var previous = points[i - 1];

            var prevValue = previous.Value;
            var currentValue = current.Value;

            var rising = prevValue < level && currentValue >= level;
            var falling = prevValue > level && currentValue <= level;
            var matchesSlope = trigger.Slope switch
            {
                TriggerSlope.Rising => rising,
                TriggerSlope.Falling => falling,
                TriggerSlope.Either => rising || falling,
                _ => rising || falling
            };

            if (!matchesSlope)
            {
                continue;
            }

            var polarityOk = trigger.Polarity switch
            {
                TriggerPolarity.Positive => currentValue - level >= -trigger.Hysteresis,
                TriggerPolarity.Negative => currentValue - level <= trigger.Hysteresis,
                _ => true
            };

            if (!polarityOk)
            {
                continue;
            }

            timestamp = InterpolateTimestamp(previous, current, level);
            return true;
        }

        return false;
    }

    private static bool TryDetectPulse(IList<MetricPoint> points, TriggerConfiguration trigger, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var level = ResolveLevel(points, trigger);
        var minWidth = Math.Max(0, trigger.MinimumPulseWidthSeconds);
        var maxWidth = trigger.MaximumPulseWidthSeconds <= 0 ? double.PositiveInfinity : trigger.MaximumPulseWidthSeconds;

        DateTimeOffset? start = null;

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];

            var prevHigh = previous.Value >= level;
            var currentHigh = current.Value >= level;

            if (!start.HasValue && !prevHigh && currentHigh)
            {
                start = InterpolateTimestamp(previous, current, level);
                continue;
            }

            if (start.HasValue && prevHigh && !currentHigh)
            {
                var end = InterpolateTimestamp(previous, current, level);
                var widthSeconds = (end - start.Value).TotalSeconds;
                if (widthSeconds >= minWidth && widthSeconds <= maxWidth)
                {
                    timestamp = end;
                    return true;
                }

                start = null;
            }
        }

        return false;
    }

    private static bool TryDetectVideo(IList<MetricPoint> points, TriggerConfiguration trigger, out DateTimeOffset timestamp)
    {
        timestamp = default;

        if (trigger.VideoLineFrequency <= 0)
        {
            return false;
        }

        var expectedPeriod = 1.0 / trigger.VideoLineFrequency;
        var tolerance = Math.Max(expectedPeriod * trigger.VideoTolerancePercentage, expectedPeriod * 0.05);
        var level = ResolveLevel(points, trigger);
        var edges = CollectEdgeTimes(points, trigger, level);

        if (edges.Count < 2)
        {
            return false;
        }

        for (var i = edges.Count - 1; i >= 1; i--)
        {
            var delta = (edges[i] - edges[i - 1]).TotalSeconds;
            if (Math.Abs(delta - expectedPeriod) <= tolerance)
            {
                timestamp = edges[i];
                return true;
            }
        }

        return false;
    }

    private static bool TryDetectLogic(IList<MetricPoint> points, TriggerConfiguration trigger, IReadOnlyList<TimelineSeries> allSeries, out DateTimeOffset timestamp)
    {
        timestamp = default;

        var (pattern, crossConditions) = ParseLogicPattern(trigger.LogicPattern);
        if (string.IsNullOrWhiteSpace(pattern))
        {
            pattern = "H";
        }

        var level = ResolveLevel(points, trigger);
        var states = BuildLogicStates(points, level);
        if (states.Count < pattern.Length)
        {
            return false;
        }

        var maxOffset = states.Count - pattern.Length;
        var minOffset = Math.Max(0, states.Count - trigger.LogicSampleLength - pattern.Length + 1);

        for (var offset = maxOffset; offset >= minOffset; offset--)
        {
            var matches = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                var stateIndex = offset + j;
                if (stateIndex >= states.Count)
                {
                    matches = false;
                    break;
                }

                var desired = char.ToUpperInvariant(pattern[j]);
                if (desired == 'X')
                {
                    continue;
                }

                var actual = states[stateIndex].state;
                if (actual != desired)
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            var candidateTime = states[offset + pattern.Length - 1].timestamp;

            if (crossConditions.Count == 0)
            {
                timestamp = candidateTime;
                return true;
            }

            if (ValidateCrossConditions(allSeries, crossConditions, candidateTime, level))
            {
                timestamp = candidateTime;
                return true;
            }
        }

        return false;
    }

    private static bool TryDetectRunt(IList<MetricPoint> points, TriggerConfiguration trigger, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var low = Math.Min(trigger.RuntLowLevel, trigger.RuntHighLevel);
        var high = Math.Max(trigger.RuntLowLevel, trigger.RuntHighLevel);

        DateTimeOffset? rise = null;
        var crossedHigh = false;

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];

            var prevAboveLow = previous.Value >= low;
            var currentAboveLow = current.Value >= low;

            if (!rise.HasValue && !prevAboveLow && currentAboveLow)
            {
                rise = InterpolateTimestamp(previous, current, low);
                crossedHigh = previous.Value >= high || current.Value >= high;
                continue;
            }

            if (rise.HasValue)
            {
                if (!crossedHigh && (previous.Value >= high || current.Value >= high))
                {
                    crossedHigh = true;
                }

                if (prevAboveLow && !currentAboveLow)
                {
                    if (!crossedHigh)
                    {
                        timestamp = InterpolateTimestamp(previous, current, low);
                        return true;
                    }

                    rise = null;
                    crossedHigh = false;
                }
            }
        }

        return false;
    }

    private static bool TryDetectWindow(IList<MetricPoint> points, TriggerConfiguration trigger, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var low = Math.Min(trigger.WindowLow, trigger.WindowHigh);
        var high = Math.Max(trigger.WindowLow, trigger.WindowHigh);

        if (high - low < 1e-12)
        {
            return false;
        }

        var previousInside = points[0].Value >= low && points[0].Value <= high;

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];
            var currentInside = current.Value >= low && current.Value <= high;

            if (previousInside == currentInside)
            {
                continue;
            }

            if (!previousInside && currentInside)
            {
                if (trigger.Slope == TriggerSlope.Falling)
                {
                    previousInside = currentInside;
                    continue;
                }

                var boundary = previous.Value < low ? low : high;
                timestamp = InterpolateTimestamp(previous, current, boundary);
                return true;
            }

            if (previousInside && !currentInside)
            {
                if (trigger.Slope == TriggerSlope.Rising)
                {
                    previousInside = currentInside;
                    continue;
                }

                var boundary = current.Value < low ? low : high;
                timestamp = InterpolateTimestamp(previous, current, boundary);
                return true;
            }

            previousInside = currentInside;
        }

        return false;
    }

    private static bool TryDetectPattern(IList<MetricPoint> points, TriggerConfiguration trigger, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var patternValues = ParseDoubleList(trigger.PatternSequence);
        if (patternValues.Count == 0)
        {
            return false;
        }

        var tolerance = Math.Max(trigger.PatternTolerance, 0.000001);

        for (var i = points.Count - patternValues.Count; i >= 0; i--)
        {
            var match = true;
            for (var j = 0; j < patternValues.Count; j++)
            {
                var expected = patternValues[j];
                var actual = points[i + j].Value;
                if (!double.IsFinite(actual))
                {
                    match = false;
                    break;
                }

                var allowedDelta = Math.Max(Math.Abs(expected) * tolerance, tolerance);
                if (Math.Abs(actual - expected) > allowedDelta)
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                timestamp = points[i + patternValues.Count - 1].Timestamp;
                return true;
            }
        }

        return false;
    }

    private static bool TryDetectSerial(IList<MetricPoint> points, TriggerConfiguration trigger, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (trigger.SerialBaudRate <= 0)
        {
            return false;
        }

        var bitTimeSeconds = 1.0 / trigger.SerialBaudRate;
        var requiredDuration = (trigger.SerialBitCount + 1) * bitTimeSeconds;
        DateTimeOffset? startEdge = null;

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];

            var prevHigh = previous.Value >= trigger.SerialThreshold;
            var currentHigh = current.Value >= trigger.SerialThreshold;

            if (!startEdge.HasValue && prevHigh && !currentHigh)
            {
                startEdge = InterpolateTimestamp(previous, current, trigger.SerialThreshold);
                continue;
            }

            if (startEdge.HasValue)
            {
                var elapsed = (current.Timestamp - startEdge.Value).TotalSeconds;
                if (elapsed >= requiredDuration)
                {
                    timestamp = startEdge.Value + TimeSpan.FromSeconds(requiredDuration);
                    return true;
                }
            }
        }

        if (startEdge.HasValue)
        {
            var last = points[^1];
            var elapsed = (last.Timestamp - startEdge.Value).TotalSeconds;
            if (elapsed >= requiredDuration)
            {
                timestamp = startEdge.Value + TimeSpan.FromSeconds(requiredDuration);
                return true;
            }
        }

        return false;
    }

    private static bool TryDetectVisual(IList<MetricPoint> points, TriggerConfiguration trigger, out DateTimeOffset timestamp)
    {
        timestamp = default;

        var template = ParseDoubleList(trigger.VisualTemplate)
            .Select(static v => double.IsFinite(v) ? Math.Clamp(v, 0, 1) : 0)
            .ToList();

        if (template.Count == 0)
        {
            return false;
        }

        var required = template.Count;
        if (points.Count < required)
        {
            return false;
        }

        var tolerance = Math.Max(trigger.VisualTolerance, 0.0001);

        for (var i = points.Count - required; i >= 0; i--)
        {
            var window = new List<MetricPoint>(required);
            for (var j = 0; j < required; j++)
            {
                window.Add(points[i + j]);
            }

            var min = window.Min(static p => p.Value);
            var max = window.Max(static p => p.Value);

            if (!double.IsFinite(min) || !double.IsFinite(max) || Math.Abs(max - min) < 1e-12)
            {
                continue;
            }

            var totalError = 0.0;
            for (var j = 0; j < required; j++)
            {
                var normalized = (window[j].Value - min) / (max - min);
                var delta = Math.Abs(normalized - template[j]);
                totalError += delta;
            }

            var averageError = totalError / required;
            if (averageError <= tolerance)
            {
                timestamp = window[^1].Timestamp;
                return true;
            }
        }

        return false;
    }

    private static List<(char state, DateTimeOffset timestamp)> BuildLogicStates(IList<MetricPoint> points, double level)
    {
        var list = new List<(char state, DateTimeOffset timestamp)>(points.Count);
        foreach (var point in points)
        {
            var state = point.Value >= level ? 'H' : 'L';
            list.Add((state, point.Timestamp));
        }

        return list;
    }

    private static (string pattern, List<(string name, char state)> conditions) ParseLogicPattern(string pattern)
    {
        var builder = new StringBuilder();
        var conditions = new List<(string name, char state)>();

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return (string.Empty, conditions);
        }

        var tokens = pattern.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex > 0)
            {
                var name = trimmed[..equalsIndex].Trim();
                var statePart = trimmed[(equalsIndex + 1)..].Trim();
                if (name.Length == 0 || statePart.Length == 0)
                {
                    continue;
                }

                var state = char.ToUpperInvariant(statePart[0]);
                if (state == 'H' || state == 'L')
                {
                    conditions.Add((name, state));
                }

                continue;
            }

            builder.Append(trimmed);
        }

        return (builder.ToString(), conditions);
    }

    private static bool ValidateCrossConditions(IReadOnlyList<TimelineSeries> allSeries, List<(string name, char state)> conditions, DateTimeOffset timestamp, double level)
    {
        foreach (var (name, expectedState) in conditions)
        {
            var series = allSeries.FirstOrDefault(s => string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(BuildSeriesLabel(s), name, StringComparison.OrdinalIgnoreCase));
            if (series is null)
            {
                return false;
            }

            var sample = series.Points.LastOrDefault(p => p.Timestamp <= timestamp && double.IsFinite(p.Value));
            if (sample.Timestamp <= DateTimeOffset.MinValue)
            {
                sample = series.Points.LastOrDefault(p => double.IsFinite(p.Value));
                if (sample.Timestamp <= DateTimeOffset.MinValue)
                {
                    return false;
                }
            }

            var isHigh = sample.Value >= level;
            if (expectedState == 'H' && !isHigh)
            {
                return false;
            }

            if (expectedState == 'L' && isHigh)
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildSeriesLabel(TimelineSeries series)
        => string.IsNullOrEmpty(series.TagSignature)
            ? $"{series.MeterName} • {series.InstrumentName}"
            : $"{series.MeterName} • {series.InstrumentName} [{series.TagSignature}]";

    private static List<DateTimeOffset> CollectEdgeTimes(IList<MetricPoint> points, TriggerConfiguration trigger, double level)
    {
        var edges = new List<DateTimeOffset>();

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];

            var rising = previous.Value < level && current.Value >= level;
            var falling = previous.Value > level && current.Value <= level;

            var matchesSlope = trigger.Slope switch
            {
                TriggerSlope.Rising => rising,
                TriggerSlope.Falling => falling,
                TriggerSlope.Either => rising || falling,
                _ => rising || falling
            };

            if (!matchesSlope)
            {
                continue;
            }

            var polarityOk = trigger.Polarity switch
            {
                TriggerPolarity.Positive => current.Value - level >= -trigger.Hysteresis,
                TriggerPolarity.Negative => current.Value - level <= trigger.Hysteresis,
                _ => true
            };

            if (!polarityOk)
            {
                continue;
            }

            edges.Add(InterpolateTimestamp(previous, current, level));
        }

        return edges;
    }

    private static DateTimeOffset InterpolateTimestamp(MetricPoint start, MetricPoint end, double level)
    {
        var valueDelta = end.Value - start.Value;
        if (Math.Abs(valueDelta) < double.Epsilon)
        {
            return end.Timestamp;
        }

        var fraction = (level - start.Value) / valueDelta;
        fraction = Math.Clamp(fraction, 0, 1);
        var ticks = (long)((end.Timestamp - start.Timestamp).Ticks * fraction);
        return start.Timestamp + TimeSpan.FromTicks(ticks);
    }

    private static List<double> ParseDoubleList(string input)
    {
        var list = new List<double>();
        if (string.IsNullOrWhiteSpace(input))
        {
            return list;
        }

        var parts = input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                list.Add(value);
            }
        }

        return list;
    }

    private void DrawEmptyState(DrawingContext context, Rect bounds)
    {
        var layout = CreateTextLayout("Waiting for metrics...", 16, FontWeight.Medium, Brushes.Gray, TextAlignment.Center, bounds.Width);
        var location = new Point((bounds.Width - layout.WidthIncludingTrailingWhitespace) / 2, (bounds.Height - layout.Height) / 2);
        layout.Draw(context, location);
    }

    private void DrawLaneBackground(DrawingContext context, Rect lane, int index)
    {
        var brush = new SolidColorBrush(index % 2 == 0 ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(10, 255, 255, 255));
        context.FillRectangle(brush, lane);
    }

    private void DrawSeriesLabel(DrawingContext context, TimelineSeries series, Rect labelRect)
    {
        var nameLayout = CreateTextLayout(series.DisplayName, 13, FontWeight.SemiBold, series.Stroke, TextAlignment.Left, labelRect.Width);
        nameLayout.Draw(context, labelRect.TopLeft + new Vector(12, 4));

        var latestText = $"{series.LatestValue:0.00} {series.Unit}".Trim();
        var latestLayout = CreateTextLayout(latestText, 12, FontWeight.Medium, Brushes.Gainsboro, TextAlignment.Left, labelRect.Width);
        latestLayout.Draw(context, labelRect.TopLeft + new Vector(12, labelRect.Height / 2));
    }

    private void DrawSeries(DrawingContext context, TimelineSeries series, Rect laneRect, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        if (series.Points.Count == 0)
        {
            return;
        }

        var snapshot = series.Points.ToList();
        var total = snapshot.Count;
        snapshot.RemoveAll(static p => !double.IsFinite(p.Value));
        var finite = snapshot.Count;

        if (finite == 0)
        {
            if (EnableRenderTraceLogging)
            {
                Logger.LogTrace("Renderer skipped {Display} because all {Total} points were non-finite", series.DisplayName, total);
            }
            return;
        }

        if (!TryBuildRenderPoints(snapshot, startTime, endTime, series.RenderMode, out var renderPoints, out var statsPoints))
        {
            if (EnableRenderTraceLogging)
            {
                Logger.LogTrace("Renderer skipped {Display}: no points in visible window (total={Total}, finite={Finite})", series.DisplayName, total, finite);
            }
            return;
        }

        if (EnableRenderTraceLogging)
        {
            Logger.LogTrace("Renderer drawing {Display} mode={Mode} total={Total} finite={Finite} render={Render} stats={Stats}", series.DisplayName, series.RenderMode, total, finite, renderPoints.Count, statsPoints.Count);
        }

        if (series.RenderMode == TimelineRenderMode.Step)
        {
            DrawStepSeries(context, series, laneRect, startTime, endTime, renderPoints, statsPoints);
        }
        else
        {
            DrawLineSeries(context, series, laneRect, startTime, endTime, renderPoints, statsPoints);
        }
    }

    private static bool TryBuildRenderPoints(List<MetricPoint> source, DateTimeOffset startTime, DateTimeOffset endTime, TimelineRenderMode mode, out List<MetricPoint> renderPoints, out List<MetricPoint> statsPoints)
    {
        renderPoints = new List<MetricPoint>();
        statsPoints = new List<MetricPoint>();

        if (source.Count == 0)
        {
            return false;
        }

        MetricPoint lastBefore = default;
        var hasLastBefore = false;

        for (var i = source.Count - 1; i >= 0; i--)
        {
            var candidate = source[i];
            if (candidate.Timestamp < startTime)
            {
                lastBefore = candidate;
                hasLastBefore = true;
                break;
            }
        }

        if (hasLastBefore)
        {
            renderPoints.Add(lastBefore);
        }

        foreach (var point in source)
        {
            if (point.Timestamp < startTime || point.Timestamp > endTime)
            {
                continue;
            }

            renderPoints.Add(point);
            statsPoints.Add(point);
        }

        if (renderPoints.Count == 0)
        {
            return false;
        }

        if (mode == TimelineRenderMode.Step)
        {
            if (renderPoints.Count > 0)
            {
                var first = renderPoints[0];
                if (first.Timestamp < startTime)
                {
                    first = new MetricPoint(startTime, first.Value, first.Tags);
                    renderPoints[0] = first;
                }

                var last = renderPoints[^1];
                if (last.Timestamp < endTime)
                {
                    last = new MetricPoint(endTime, last.Value, last.Tags);
                    renderPoints.Add(last);
                }

                if (renderPoints.Count == 1)
                {
                    var clone = new MetricPoint(endTime, renderPoints[0].Value, renderPoints[0].Tags);
                    renderPoints.Add(clone);
                    last = clone;
                }

                AddStatPoint(statsPoints, first);
                AddStatPoint(statsPoints, renderPoints[^1]);
            }
        }

        if (statsPoints.Count == 0)
        {
            statsPoints.Add(renderPoints[^1]);
        }

        return true;
    }

    private static void AddStatPoint(List<MetricPoint> statsPoints, MetricPoint point)
    {
        if (!statsPoints.Any(p => p.Timestamp == point.Timestamp && Math.Abs(p.Value - point.Value) < 0.0001))
        {
            statsPoints.Add(point);
        }
    }

    private void DrawLineSeries(DrawingContext context, TimelineSeries series, Rect laneRect, DateTimeOffset startTime, DateTimeOffset endTime, IList<MetricPoint> renderPoints, IList<MetricPoint> statsPoints)
    {
        var min = statsPoints.Min(p => p.Value);
        var max = statsPoints.Max(p => p.Value);

        if (Math.Abs(max - min) < 0.001)
        {
            max = min + 1;
            min -= 0.5;
        }

        var latest = renderPoints[^1];
        var latestPoint = MapPoint(latest, laneRect, startTime, endTime, min, max);

        if (renderPoints.Count == 1)
        {
            context.DrawEllipse(series.Stroke, null, latestPoint, 4, 4);
            DrawRangeAnnotations(context, laneRect, min, max, series);
            return;
        }

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        var first = renderPoints[0];
        var firstPoint = MapPoint(first, laneRect, startTime, endTime, min, max);
        ctx.BeginFigure(firstPoint, false);

        for (var i = 1; i < renderPoints.Count; i++)
        {
            var mapped = MapPoint(renderPoints[i], laneRect, startTime, endTime, min, max);
            ctx.LineTo(mapped);
        }

        ctx.EndFigure(false);

        var pen = new Pen(series.Stroke, 2) { LineJoin = PenLineJoin.Round };
        context.DrawGeometry(null, pen, geometry);

        context.DrawEllipse(series.Stroke, null, latestPoint, 3, 3);

        DrawRangeAnnotations(context, laneRect, min, max, series);
    }

    private void DrawStepSeries(DrawingContext context, TimelineSeries series, Rect laneRect, DateTimeOffset startTime, DateTimeOffset endTime, IList<MetricPoint> renderPoints, IList<MetricPoint> statsPoints)
    {
        if (renderPoints.Count < 2)
        {
            return;
        }

        var min = statsPoints.Min(p => p.Value);
        var max = statsPoints.Max(p => p.Value);

        if (Math.Abs(max - min) < 0.001)
        {
            max = min + 1;
            min -= 0.5;
        }

        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();

        var first = renderPoints[0];
        var firstPoint = MapPoint(first, laneRect, startTime, endTime, min, max);
        ctx.BeginFigure(firstPoint, false);

        for (var i = 1; i < renderPoints.Count; i++)
        {
            var prev = renderPoints[i - 1];
            var current = renderPoints[i];

            var prevPoint = MapPoint(prev, laneRect, startTime, endTime, min, max);
            var currentPoint = MapPoint(current, laneRect, startTime, endTime, min, max);

            ctx.LineTo(new Point(currentPoint.X, prevPoint.Y));
            ctx.LineTo(currentPoint);
        }

        ctx.EndFigure(false);

        var pen = new Pen(series.Stroke, 2) { LineJoin = PenLineJoin.Miter };
        context.DrawGeometry(null, pen, geometry);

        DrawRangeAnnotations(context, laneRect, min, max, series);
    }

    private Point MapPoint(MetricPoint point, Rect laneRect, DateTimeOffset startTime, DateTimeOffset endTime, double min, double max)
    {
        var totalSeconds = Math.Max(1e-9, (endTime - startTime).TotalSeconds);
        var elapsed = (point.Timestamp - startTime).TotalSeconds;
        var progress = Math.Clamp(elapsed / totalSeconds, 0, 1);
        var x = laneRect.Left + progress * laneRect.Width;
        var normalized = (point.Value - min) / (max - min);
        var y = laneRect.Bottom - normalized * (laneRect.Height - 12) - 6;
        return new Point(x, y);
    }

    private void DrawRangeAnnotations(DrawingContext context, Rect laneRect, double min, double max, TimelineSeries series)
    {
        var minLayout = CreateTextLayout($"min {min:0.00}", 11, FontWeight.Normal, Brushes.Gray, TextAlignment.Right, laneRect.Width);
        minLayout.Draw(context, new Point(laneRect.Right - minLayout.WidthIncludingTrailingWhitespace - 4, laneRect.Bottom - minLayout.Height - 2));

        var maxLayout = CreateTextLayout($"max {max:0.00}", 11, FontWeight.Normal, Brushes.Gray, TextAlignment.Right, laneRect.Width);
        maxLayout.Draw(context, new Point(laneRect.Right - maxLayout.WidthIncludingTrailingWhitespace - 4, laneRect.Top + 2));

        var avgLayout = CreateTextLayout($"avg {series.Average:0.00}", 11, FontWeight.Normal, Brushes.Gray, TextAlignment.Right, laneRect.Width);
        avgLayout.Draw(context, new Point(laneRect.Right - avgLayout.WidthIncludingTrailingWhitespace - 4, laneRect.Center.Y - avgLayout.Height / 2));
    }

    private static double MapTimeToX(DateTimeOffset timestamp, double labelWidth, double plotWidth, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var totalSeconds = Math.Max(1e-9, (endTime - startTime).TotalSeconds);
        var elapsed = (timestamp - startTime).TotalSeconds;
        var progress = Math.Clamp(elapsed / totalSeconds, 0, 1);
        return labelWidth + progress * plotWidth;
    }

    private void DrawTimeGrid(DrawingContext context, double labelWidth, double topPadding, double plotWidth, double plotHeight, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1);
        var steps = 6;

        for (var i = 0; i <= steps; i++)
        {
            var progress = (double)i / steps;
            var x = labelWidth + progress * plotWidth;
            context.DrawLine(gridPen, new Point(x, topPadding), new Point(x, topPadding + plotHeight));
        }
    }

    private void DrawTriggerMarker(DrawingContext context, double labelWidth, double topPadding, double plotWidth, double plotHeight, DateTimeOffset startTime, DateTimeOffset endTime, DateTimeOffset anchorTime)
    {
        if (anchorTime <= DateTimeOffset.MinValue)
        {
            return;
        }

        var x = MapTimeToX(anchorTime, labelWidth, plotWidth, startTime, endTime);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 153, 0)), 1.5);
        context.DrawLine(pen, new Point(x, topPadding), new Point(x, topPadding + plotHeight));

        var label = CreateTextLayout("TRIG", 11, FontWeight.SemiBold, Brushes.Orange, TextAlignment.Center, 40);
        label.Draw(context, new Point(x - label.WidthIncludingTrailingWhitespace / 2, topPadding - label.Height - 2));
    }

    private void DrawTimeAxis(DrawingContext context, double labelWidth, double topPadding, double plotWidth, double plotHeight, DateTimeOffset startTime, DateTimeOffset endTime, bool useRelative, DateTimeOffset anchorTime)
    {
        var steps = 6;
        var duration = Math.Max(1e-9, (endTime - startTime).TotalSeconds);
        for (var i = 0; i <= steps; i++)
        {
            var progress = (double)i / steps;
            var timestamp = startTime + TimeSpan.FromSeconds(progress * duration);
            string label;

            if (useRelative)
            {
                var reference = anchorTime == default ? endTime : anchorTime;
                var offsetSeconds = (timestamp - reference).TotalSeconds;
                label = FormatRelativeOffset(offsetSeconds);
            }
            else
            {
                label = FormatAbsoluteTimestamp(timestamp, duration);
            }

            var x = labelWidth + progress * plotWidth;
            var textLayout = CreateTextLayout(label, 11, FontWeight.Normal, Brushes.Gray, TextAlignment.Center, 80);
            var position = new Point(x - textLayout.WidthIncludingTrailingWhitespace / 2, topPadding + plotHeight + 6);
            textLayout.Draw(context, position);
        }
    }

    private static string FormatAbsoluteTimestamp(DateTimeOffset timestamp, double durationSeconds)
    {
        if (durationSeconds < 1)
        {
            return timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        }

        if (durationSeconds < 60)
        {
            return timestamp.ToLocalTime().ToString("HH:mm:ss");
        }

        return timestamp.ToLocalTime().ToString("HH:mm");
    }

    private static string FormatRelativeOffset(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return "0 s";
        }

        if (Math.Abs(seconds) < 1e-12)
        {
            seconds = 0;
        }

        var sign = seconds switch
        {
            > 0 => "+",
            < 0 => "-",
            _ => string.Empty
        };

        var abs = Math.Abs(seconds);

        if (abs >= 1)
        {
            return $"{sign}{abs:0.###} s";
        }

        if (abs >= 1e-3)
        {
            return $"{sign}{abs * 1_000:0.###} ms";
        }

        if (abs >= 1e-6)
        {
            return $"{sign}{abs * 1_000_000:0.###} us";
        }

        return $"{sign}{abs * 1_000_000_000:0.###} ns";
    }

    private static TextLayout CreateTextLayout(string text, double fontSize, FontWeight weight, IBrush brush, TextAlignment alignment, double maxWidth)
    {
        var typeface = new Typeface("Inter", FontStyle.Normal, weight);
        var constraint = double.IsFinite(maxWidth) && maxWidth > 0 ? maxWidth : double.PositiveInfinity;
        return new TextLayout(text, typeface, fontSize, brush, alignment, TextWrapping.NoWrap, textTrimming: null, textDecorations: null, flowDirection: FlowDirection.LeftToRight, maxWidth: constraint);
    }
}
