using System;
using System.Collections.Generic;
using System.Buffers;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Metriclonia.Monitor.Infrastructure;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Metriclonia.Monitor.Visualization;

public class TimelinePlotControl : Control
{
    private static readonly ILogger Logger = Log.For<TimelinePlotControl>();
    private const bool EnableRenderTraceLogging = false;

    private static readonly ImmutableSolidColorBrush PlotBackgroundBrush = new(Color.FromArgb(240, 12, 16, 24));
    private static readonly ImmutablePen GridPen = new(new ImmutableSolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1);
    private static readonly ImmutablePen TriggerMarkerPen = new(new ImmutableSolidColorBrush(Color.FromArgb(200, 255, 153, 0)), 1.5);

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
        context.FillRectangle(PlotBackgroundBrush, bounds);

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
        if (!SeriesRenderBuilder.TryCreate(series, laneRect, startTime, endTime, out var builder))
        {
            return;
        }

        var renderBuilder = builder;
        if (renderBuilder is null)
        {
            return;
        }

        using (renderBuilder)
        {
            var operationScheduled = false;

            if (renderBuilder.PointCount >= 2)
            {
                var operation = renderBuilder.CreateSkiaOperation(series);
                if (operation is not null)
                {
                    context.Custom(operation);
                    operationScheduled = true;
                }
            }

            if (!operationScheduled)
            {
                renderBuilder.DrawFallback(context, series);
            }

            if (series.RenderMode == TimelineRenderMode.Line && renderBuilder.HasLatestPoint)
            {
                context.DrawEllipse(series.Stroke, null, renderBuilder.LatestPoint, 3, 3);
            }

            DrawRangeAnnotations(context, laneRect, renderBuilder.Min, renderBuilder.Max, series);
        }
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
        var steps = 6;

        for (var i = 0; i <= steps; i++)
        {
            var progress = (double)i / steps;
            var x = labelWidth + progress * plotWidth;
            context.DrawLine(GridPen, new Point(x, topPadding), new Point(x, topPadding + plotHeight));
        }
    }

    private void DrawTriggerMarker(DrawingContext context, double labelWidth, double topPadding, double plotWidth, double plotHeight, DateTimeOffset startTime, DateTimeOffset endTime, DateTimeOffset anchorTime)
    {
        if (anchorTime <= DateTimeOffset.MinValue)
        {
            return;
        }

        var x = MapTimeToX(anchorTime, labelWidth, plotWidth, startTime, endTime);
        context.DrawLine(TriggerMarkerPen, new Point(x, topPadding), new Point(x, topPadding + plotHeight));

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

    private sealed class SeriesRenderBuilder : IDisposable
    {
        private readonly TimelineSeries _series;
        private readonly Rect _laneRect;
        private readonly DateTimeOffset _startTime;
        private readonly DateTimeOffset _endTime;
        private readonly TimelineRenderMode _mode;

        private Point[]? _points;
        private SKPoint[]? _skPoints;
        private int _count;
        private bool _skTransferred;
        private Point _latestPoint;
        private bool _hasLatest;

        private SeriesRenderBuilder(TimelineSeries series, Rect laneRect, DateTimeOffset startTime, DateTimeOffset endTime)
        {
            _series = series;
            _laneRect = laneRect;
            _startTime = startTime;
            _endTime = endTime;
            _mode = series.RenderMode;
        }

        public static bool TryCreate(TimelineSeries series, Rect laneRect, DateTimeOffset startTime, DateTimeOffset endTime, out SeriesRenderBuilder? builder)
        {
            var instance = new SeriesRenderBuilder(series, laneRect, startTime, endTime);
            if (!instance.Build())
            {
                instance.Dispose();
                builder = null;
                return false;
            }

            builder = instance;
            return true;
        }

        public double Min { get; private set; }

        public double Max { get; private set; }

        public int PointCount => _count;

        public bool HasLatestPoint => _hasLatest;

        public Point LatestPoint => _latestPoint;

        private bool Build()
        {
            var source = _series.Points;
            var total = source.Count;
            if (total == 0)
            {
                return false;
            }

            var metricsPool = ArrayPool<MetricPoint>.Shared;
            var buffer = metricsPool.Rent(total + 2);
            var length = 0;
            MetricPoint? lastBefore = null;

            for (var i = 0; i < total; i++)
            {
                var point = source[i];
                if (!double.IsFinite(point.Value) || point.Timestamp <= DateTimeOffset.MinValue)
                {
                    continue;
                }

                if (point.Timestamp < _startTime)
                {
                    lastBefore = point;
                    continue;
                }

                if (point.Timestamp > _endTime)
                {
                    break;
                }

                buffer[length++] = point;
            }

            if (length == 0)
            {
                if (lastBefore is null)
                {
                    metricsPool.Return(buffer);
                    return false;
                }

                buffer[length++] = lastBefore.Value;
            }

            if (lastBefore is not null)
            {
                Array.Copy(buffer, 0, buffer, 1, length);
                buffer[0] = lastBefore.Value;
                length++;
            }

            if (_mode == TimelineRenderMode.Step)
            {
                var first = buffer[0];
                if (first.Timestamp < _startTime)
                {
                    buffer[0] = new MetricPoint(_startTime, first.Value, first.Tags);
                }

                var last = buffer[length - 1];
                if (last.Timestamp < _endTime)
                {
                    buffer[length++] = new MetricPoint(_endTime, last.Value, last.Tags);
                }

                if (length == 1)
                {
                    buffer[length++] = new MetricPoint(_endTime, buffer[0].Value, buffer[0].Tags);
                }
            }
            else if (length == 1 && buffer[0].Timestamp < _startTime)
            {
                metricsPool.Return(buffer);
                return false;
            }

            var min = double.PositiveInfinity;
            var max = double.NegativeInfinity;
            var anyWindow = false;

            for (var i = 0; i < length; i++)
            {
                var point = buffer[i];
                if (point.Timestamp < _startTime || point.Timestamp > _endTime)
                {
                    continue;
                }

                var value = point.Value;
                if (!double.IsFinite(value))
                {
                    continue;
                }

                anyWindow = true;
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }

            if (!anyWindow)
            {
                metricsPool.Return(buffer);
                return false;
            }

            if (Math.Abs(max - min) < 0.001)
            {
                max = min + 1;
                min -= 0.5;
            }

            var skPool = ArrayPool<SKPoint>.Shared;
            var pointPool = ArrayPool<Point>.Shared;
            var skPoints = skPool.Rent(length);
            var avaloniaPoints = pointPool.Rent(length);

            var durationSeconds = Math.Max(1e-9, (_endTime - _startTime).TotalSeconds);
            var width = _laneRect.Width;
            var height = Math.Max(1, _laneRect.Height - 12);
            var left = _laneRect.Left;
            var bottom = _laneRect.Bottom - 6;

            Point latestPoint = default;
            var hasLatest = false;

            for (var i = 0; i < length; i++)
            {
                var metric = buffer[i];
                var elapsed = (metric.Timestamp - _startTime).TotalSeconds;
                var progress = Math.Clamp(elapsed / durationSeconds, 0, 1);
                var x = left + progress * width;
                var normalized = (metric.Value - min) / (max - min);
                if (!double.IsFinite(normalized))
                {
                    normalized = 0;
                }

                var y = bottom - normalized * height;
                var mapped = new Point(x, y);
                avaloniaPoints[i] = mapped;
                skPoints[i] = new SKPoint((float)x, (float)y);

                if (metric.Timestamp <= _endTime)
                {
                    latestPoint = mapped;
                    hasLatest = true;
                }
            }

            if (!hasLatest && length > 0)
            {
                latestPoint = avaloniaPoints[length - 1];
                hasLatest = true;
            }

            metricsPool.Return(buffer);

            _points = avaloniaPoints;
            _skPoints = skPoints;
            _count = length;
            _latestPoint = latestPoint;
            _hasLatest = hasLatest;
            Min = min;
            Max = max;

            return true;
        }

        public TimelineSeriesDrawOperation? CreateSkiaOperation(TimelineSeries series)
        {
            if (_skPoints is null || _count == 0)
            {
                return null;
            }

            if (_count < 2 && _mode != TimelineRenderMode.Step)
            {
                return null;
            }

            _skTransferred = true;
            var operation = new TimelineSeriesDrawOperation(_laneRect, _skPoints, _count, series.Color, _mode);
            _skPoints = null;
            return operation;
        }

        public void DrawFallback(DrawingContext context, TimelineSeries series)
        {
            if (_points is null || _count == 0)
            {
                return;
            }

            if (_count == 1)
            {
                context.DrawEllipse(series.Stroke, null, _points[0], 4, 4);
                return;
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(_points[0], false);

                if (_mode == TimelineRenderMode.Step)
                {
                    for (var i = 1; i < _count; i++)
                    {
                        var prev = _points[i - 1];
                        var current = _points[i];
                        ctx.LineTo(new Point(current.X, prev.Y));
                        ctx.LineTo(current);
                    }
                }
                else
                {
                    for (var i = 1; i < _count; i++)
                    {
                        ctx.LineTo(_points[i]);
                    }
                }

                ctx.EndFigure(false);
            }

            var pen = new Pen(series.Stroke, 2)
            {
                LineJoin = _mode == TimelineRenderMode.Step ? PenLineJoin.Miter : PenLineJoin.Round
            };

            context.DrawGeometry(null, pen, geometry);
        }

        public void Dispose()
        {
            if (_points is not null)
            {
                ArrayPool<Point>.Shared.Return(_points);
                _points = null;
            }

            if (!_skTransferred && _skPoints is not null)
            {
                ArrayPool<SKPoint>.Shared.Return(_skPoints);
            }

            _skPoints = null;
        }
    }

    private sealed class TimelineSeriesDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly SKPoint[] _points;
        private readonly int _count;
        private readonly Color _color;
        private readonly TimelineRenderMode _mode;

        public TimelineSeriesDrawOperation(Rect bounds, SKPoint[] points, int count, Color color, TimelineRenderMode mode)
        {
            _bounds = bounds;
            _points = points;
            _count = count;
            _color = color;
            _mode = mode;
        }

        public Rect Bounds => _bounds;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (feature is null)
            {
                DrawFallback(context);
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;

            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(_color.R, _color.G, _color.B, _color.A),
                StrokeWidth = 2f,
                StrokeJoin = _mode == TimelineRenderMode.Step ? SKStrokeJoin.Miter : SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };

            if (_count <= 1 && _mode != TimelineRenderMode.Step)
            {
                canvas.DrawCircle(_points[0], 3f, paint);
                return;
            }

            using var path = new SKPath();
            path.MoveTo(_points[0]);

            if (_mode == TimelineRenderMode.Step)
            {
                for (var i = 1; i < _count; i++)
                {
                    var prev = _points[i - 1];
                    var current = _points[i];
                    path.LineTo(new SKPoint(current.X, prev.Y));
                    path.LineTo(current);
                }
            }
            else
            {
                for (var i = 1; i < _count; i++)
                {
                    path.LineTo(_points[i]);
                }
            }

            canvas.DrawPath(path, paint);
        }

        public void Dispose()
        {
            ArrayPool<SKPoint>.Shared.Return(_points);
        }

        public bool HitTest(Point p) => _bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

        private void DrawFallback(ImmediateDrawingContext context)
        {
            if (_count == 0)
            {
                return;
            }

            var brush = new ImmutableSolidColorBrush(_color);

            if (_count == 1 && _mode != TimelineRenderMode.Step)
            {
                var point = new Point(_points[0].X, _points[0].Y);
                context.DrawEllipse(brush, null, point, 3, 3);
                return;
            }

            var pen = new Pen(new SolidColorBrush(_color), 2)
            {
                LineJoin = _mode == TimelineRenderMode.Step ? PenLineJoin.Miter : PenLineJoin.Round
            }.ToImmutable();

            if (_mode == TimelineRenderMode.Step)
            {
                for (var i = 1; i < _count; i++)
                {
                    var prev = _points[i - 1];
                    var current = _points[i];
                    var prevPoint = new Point(prev.X, prev.Y);
                    var horizontalEnd = new Point(current.X, prev.Y);
                    var currentPoint = new Point(current.X, current.Y);
                    context.DrawLine(pen, prevPoint, horizontalEnd);
                    context.DrawLine(pen, horizontalEnd, currentPoint);
                }
            }
            else
            {
                for (var i = 1; i < _count; i++)
                {
                    var start = new Point(_points[i - 1].X, _points[i - 1].Y);
                    var end = new Point(_points[i].X, _points[i].Y);
                    context.DrawLine(pen, start, end);
                }
            }
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
