using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Metriclonia.Monitor.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Metriclonia.Monitor.Visualization;

public sealed class FlameChartControl : Control
{
    private static readonly ILogger Logger = Log.For<FlameChartControl>();

    public static readonly StyledProperty<FlameChartDefinition?> DefinitionProperty =
        AvaloniaProperty.Register<FlameChartControl, FlameChartDefinition?>(nameof(Definition));

    public static readonly StyledProperty<IEnumerable<TimelineSeries>?> SeriesProperty =
        AvaloniaProperty.Register<FlameChartControl, IEnumerable<TimelineSeries>?>(nameof(Series));

    public static readonly StyledProperty<IEnumerable<ActivitySeries>?> ActivitiesProperty =
        AvaloniaProperty.Register<FlameChartControl, IEnumerable<ActivitySeries>?>(nameof(Activities));

    public static readonly StyledProperty<double> VisibleDurationSecondsProperty =
        AvaloniaProperty.Register<FlameChartControl, double>(nameof(VisibleDurationSeconds), 30d);

    private readonly HashSet<TimelineSeries> _metricSubscriptions = new();
    private readonly HashSet<ActivitySeries> _activitySubscriptions = new();
    private readonly Dictionary<ActivitySeries, INotifyCollectionChanged?> _activityPointSubscriptions = new();
    private INotifyCollectionChanged? _seriesCollectionSubscription;
    private INotifyCollectionChanged? _activityCollectionSubscription;

    static FlameChartControl()
    {
        AffectsRender<FlameChartControl>(DefinitionProperty, VisibleDurationSecondsProperty);
    }

    public FlameChartDefinition? Definition
    {
        get => GetValue(DefinitionProperty);
        set => SetValue(DefinitionProperty, value);
    }

    public IEnumerable<TimelineSeries>? Series
    {
        get => GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public IEnumerable<ActivitySeries>? Activities
    {
        get => GetValue(ActivitiesProperty);
        set => SetValue(ActivitiesProperty, value);
    }

    public double VisibleDurationSeconds
    {
        get => GetValue(VisibleDurationSecondsProperty);
        set => SetValue(VisibleDurationSecondsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SeriesProperty)
        {
            if (change.OldValue is IEnumerable<TimelineSeries> oldSeries)
            {
                DetachMetricSeries(oldSeries);
            }

            if (change.NewValue is IEnumerable<TimelineSeries> newSeries)
            {
                AttachMetricSeries(newSeries);
            }

            InvalidateVisual();
        }
        else if (change.Property == ActivitiesProperty)
        {
            if (change.OldValue is IEnumerable<ActivitySeries> oldActivities)
            {
                DetachActivitySeries(oldActivities);
            }

            if (change.NewValue is IEnumerable<ActivitySeries> newActivities)
            {
                AttachActivitySeries(newActivities);
            }

            InvalidateVisual();
        }
        else if (change.Property == DefinitionProperty || change.Property == VisibleDurationSecondsProperty)
        {
            InvalidateVisual();
        }
    }

    private void AttachMetricSeries(IEnumerable<TimelineSeries> series)
    {
        if (series is INotifyCollectionChanged notify)
        {
            _seriesCollectionSubscription = notify;
            notify.CollectionChanged += OnMetricSeriesCollectionChanged;
        }

        foreach (var item in series)
        {
            SubscribeMetricSeries(item);
        }
    }

    private void DetachMetricSeries(IEnumerable<TimelineSeries> series)
    {
        if (_seriesCollectionSubscription is not null)
        {
            _seriesCollectionSubscription.CollectionChanged -= OnMetricSeriesCollectionChanged;
            _seriesCollectionSubscription = null;
        }

        foreach (var item in series)
        {
            UnsubscribeMetricSeries(item);
        }
    }

    private void AttachActivitySeries(IEnumerable<ActivitySeries> activities)
    {
        if (activities is INotifyCollectionChanged notify)
        {
            _activityCollectionSubscription = notify;
            notify.CollectionChanged += OnActivitySeriesCollectionChanged;
        }

        foreach (var item in activities)
        {
            SubscribeActivitySeries(item);
        }
    }

    private void DetachActivitySeries(IEnumerable<ActivitySeries> activities)
    {
        if (_activityCollectionSubscription is not null)
        {
            _activityCollectionSubscription.CollectionChanged -= OnActivitySeriesCollectionChanged;
            _activityCollectionSubscription = null;
        }

        foreach (var item in activities)
        {
            UnsubscribeActivitySeries(item);
        }
    }

    private void OnMetricSeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is { Count: > 0 })
        {
            foreach (var series in e.OldItems.OfType<TimelineSeries>())
            {
                UnsubscribeMetricSeries(series);
            }
        }

        if (e.NewItems is { Count: > 0 })
        {
            foreach (var series in e.NewItems.OfType<TimelineSeries>())
            {
                SubscribeMetricSeries(series);
            }
        }

        InvalidateVisual();
    }

    private void OnActivitySeriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is { Count: > 0 })
        {
            foreach (var series in e.OldItems.OfType<ActivitySeries>())
            {
                UnsubscribeActivitySeries(series);
            }
        }

        if (e.NewItems is { Count: > 0 })
        {
            foreach (var series in e.NewItems.OfType<ActivitySeries>())
            {
                SubscribeActivitySeries(series);
            }
        }

        InvalidateVisual();
    }

    private void SubscribeMetricSeries(TimelineSeries series)
    {
        if (!_metricSubscriptions.Add(series))
        {
            return;
        }

        series.PropertyChanged += OnMetricSeriesPropertyChanged;
        series.PointsChanged += OnMetricPointsChanged;
    }

    private void UnsubscribeMetricSeries(TimelineSeries series)
    {
        if (!_metricSubscriptions.Remove(series))
        {
            return;
        }

        series.PropertyChanged -= OnMetricSeriesPropertyChanged;
        series.PointsChanged -= OnMetricPointsChanged;
    }

    private void SubscribeActivitySeries(ActivitySeries series)
    {
        if (!_activitySubscriptions.Add(series))
        {
            return;
        }

        series.PropertyChanged += OnActivitySeriesPropertyChanged;

        if (series.Points is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += OnActivityPointsChanged;
            _activityPointSubscriptions[series] = notify;
        }
        else
        {
            _activityPointSubscriptions[series] = null;
        }
    }

    private void UnsubscribeActivitySeries(ActivitySeries series)
    {
        if (!_activitySubscriptions.Remove(series))
        {
            return;
        }

        series.PropertyChanged -= OnActivitySeriesPropertyChanged;

        if (_activityPointSubscriptions.TryGetValue(series, out var notify) && notify is not null)
        {
            notify.CollectionChanged -= OnActivityPointsChanged;
        }

        _activityPointSubscriptions.Remove(series);
    }

    private void OnMetricSeriesPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => InvalidateVisual();

    private void OnMetricPointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    private void OnActivitySeriesPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => InvalidateVisual();

    private void OnActivityPointsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(245, 8, 11, 18)), bounds);

        var definition = Definition;
        if (definition is null)
        {
            DrawEmptyState(context, bounds, "No flame chart definition configured");
            return;
        }

        if (definition.Lanes.Count == 0)
        {
            DrawEmptyState(context, bounds, "No lanes configured");
            return;
        }

        var laneInfos = ResolveLaneInfos(definition);
        if (laneInfos.Count == 0)
        {
            DrawEmptyState(context, bounds, "Waiting for matching metrics or activities...");
            return;
        }

        var visibleSeconds = Math.Max(1, VisibleDurationSeconds);
        var latestTimestamp = GetLatestRelevantTimestamp(laneInfos);
        var endTime = latestTimestamp;
        var startTime = endTime - TimeSpan.FromSeconds(visibleSeconds);

        foreach (var lane in laneInfos)
        {
            PopulateLaneSegments(lane, startTime, endTime);
        }

        var labelWidth = 220;
        var horizontalPadding = 18;
        var topPadding = 24;
        var bottomPadding = 48;
        var laneSpacing = 8;

        var plotWidth = Math.Max(40, bounds.Width - labelWidth - horizontalPadding * 2);
        var plotHeight = Math.Max(40, bounds.Height - topPadding - bottomPadding);
        var laneHeight = laneInfos.Count > 0
            ? (plotHeight - laneSpacing * Math.Max(0, laneInfos.Count - 1)) / laneInfos.Count
            : plotHeight;

        if (laneHeight < 24)
        {
            laneHeight = 24;
        }

        DrawTimeGrid(context, labelWidth, topPadding, plotWidth, plotHeight);

        for (var index = 0; index < laneInfos.Count; index++)
        {
            var laneInfo = laneInfos[index];
            var laneTop = topPadding + index * (laneHeight + laneSpacing);
            var laneRect = new Rect(labelWidth, laneTop, plotWidth, laneHeight);
            DrawLaneBackground(context, laneRect, index);
            DrawLaneLabel(context, laneInfo, new Rect(horizontalPadding, laneTop, labelWidth - horizontalPadding, laneHeight));
            DrawSegments(context, laneInfo, laneRect, startTime, endTime);
        }

        DrawTimeAxis(context, labelWidth, topPadding, plotWidth, plotHeight, startTime, endTime);

        var titleLayout = CreateTextLayout(definition.Title, 18, FontWeight.SemiBold, Brushes.Gainsboro, TextAlignment.Left, bounds.Width - horizontalPadding * 2);
        titleLayout.Draw(context, new Point(horizontalPadding, 10));
    }

    private List<LaneRenderInfo> ResolveLaneInfos(FlameChartDefinition definition)
    {
        var result = new List<LaneRenderInfo>(definition.Lanes.Count);

        foreach (var lane in definition.Lanes)
        {
            LaneRenderInfo? info = lane.SourceType switch
            {
                FlameLaneSourceType.Metric => BuildMetricLane(lane),
                FlameLaneSourceType.Activity => BuildActivityLane(lane),
                _ => null
            };

            if (info is null)
            {
                continue;
            }

            result.Add(info);
        }

        return result;
    }

    private LaneRenderInfo? BuildMetricLane(FlameLaneDefinition lane)
    {
        var series = FindMetricSeries(lane.SourceKey);
        if (series is null)
        {
            Logger.LogTrace("Flame chart lane {Lane} skipped: metric {Instrument} not found", lane.DisplayName, lane.SourceKey);
            return null;
        }

        var summary = series.Points.Count > 0
            ? $"latest {series.LatestValue:0.###} {series.Unit}".Trim()
            : "awaiting samples";

        return new LaneRenderInfo(lane)
        {
            MetricSeries = series,
            Fill = series.Fill,
            Stroke = series.Stroke,
            Pen = new Pen(series.Stroke, 1),
            Summary = summary
        };
    }

    private LaneRenderInfo? BuildActivityLane(FlameLaneDefinition lane)
    {
        var series = FindActivitySeries(lane.SourceKey);
        if (series is null)
        {
            Logger.LogTrace("Flame chart lane {Lane} skipped: activity {Activity} not found", lane.DisplayName, lane.SourceKey);
            return null;
        }

        var summary = series.HasObservations
            ? $"last {series.LastDurationMs:0.###} ms ({series.LastStatus})"
            : "awaiting events";

        return new LaneRenderInfo(lane)
        {
            ActivitySeries = series,
            Fill = series.AccentBrush,
            Stroke = series.Stroke,
            Pen = new Pen(series.Stroke, 1),
            Summary = summary
        };
    }

    private void PopulateLaneSegments(LaneRenderInfo lane, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        if (lane.MetricSeries is { Points.Count: > 0 } metricSeries)
        {
            for (var i = metricSeries.Points.Count - 1; i >= 0; i--)
            {
                var point = metricSeries.Points[i];
                if (!double.IsFinite(point.Value) || point.Value <= 0)
                {
                    continue;
                }

                var end = point.Timestamp;
                if (end < startTime)
                {
                    break;
                }

                if (end > endTime + TimeSpan.FromSeconds(VisibleDurationSeconds))
                {
                    continue;
                }

                var duration = TimeSpan.FromMilliseconds(point.Value);
                var start = end - duration;

                var clampedStart = start < startTime ? startTime : start;
                var clampedEnd = end > endTime ? endTime : end;

                if (clampedEnd <= clampedStart)
                {
                    continue;
                }

                var label = $"{point.Value:0.###} {metricSeries.Unit}".Trim();
                lane.Segments.Add(new FlameSegment(clampedStart, clampedEnd, point.Value, label));
            }

            lane.Segments.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            return;
        }

        if (lane.ActivitySeries is { Points.Count: > 0 } activitySeries)
        {
            for (var i = activitySeries.Points.Count - 1; i >= 0; i--)
            {
                var point = activitySeries.Points[i];
                var start = point.Timestamp;
                var end = start + TimeSpan.FromMilliseconds(point.DurationMilliseconds);

                if (end < startTime)
                {
                    break;
                }

                if (start > endTime)
                {
                    continue;
                }

                var clampedStart = start < startTime ? startTime : start;
                var clampedEnd = end > endTime ? endTime : end;

                if (clampedEnd <= clampedStart)
                {
                    continue;
                }

                var label = $"{point.DurationMilliseconds:0.###} ms";
                lane.Segments.Add(new FlameSegment(clampedStart, clampedEnd, point.DurationMilliseconds, label));
            }

            lane.Segments.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        }
    }

    private TimelineSeries? FindMetricSeries(string instrumentName)
    {
        if (Series is null)
        {
            return null;
        }

        TimelineSeries? taggedCandidate = null;

        foreach (var series in Series)
        {
            if (!string.Equals(series.InstrumentName, instrumentName, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(series.TagSignature))
            {
                return series;
            }

            taggedCandidate ??= series;
        }

        return taggedCandidate;
    }

    private ActivitySeries? FindActivitySeries(string activityName)
    {
        if (Activities is null)
        {
            return null;
        }

        foreach (var series in Activities)
        {
            if (string.Equals(series.Name, activityName, StringComparison.Ordinal))
            {
                return series;
            }
        }

        return null;
    }

    private DateTimeOffset GetLatestRelevantTimestamp(IReadOnlyList<LaneRenderInfo> lanes)
    {
        var latest = DateTimeOffset.UtcNow;

        foreach (var lane in lanes)
        {
            if (lane.MetricSeries is { Points.Count: > 0 })
            {
                var timestamp = lane.MetricSeries.Points[^1].Timestamp;
                if (timestamp > latest)
                {
                    latest = timestamp;
                }
            }

            if (lane.ActivitySeries is { Points.Count: > 0 })
            {
                var point = lane.ActivitySeries.Points[^1];
                var end = point.Timestamp + TimeSpan.FromMilliseconds(point.DurationMilliseconds);
                if (end > latest)
                {
                    latest = end;
                }
            }
        }

        return latest;
    }

    private void DrawLaneBackground(DrawingContext context, Rect laneRect, int index)
    {
        var color = index % 2 == 0 ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 255, 255, 255);
        context.FillRectangle(new SolidColorBrush(color), laneRect);
    }

    private void DrawLaneLabel(DrawingContext context, LaneRenderInfo lane, Rect labelRect)
    {
        var accent = lane.Stroke ?? Brushes.Gainsboro;
        var titleLayout = CreateTextLayout(lane.Definition.DisplayName, 13, FontWeight.SemiBold, accent, TextAlignment.Left, labelRect.Width - 12);
        titleLayout.Draw(context, labelRect.TopLeft + new Vector(8, 2));

        if (!string.IsNullOrWhiteSpace(lane.Summary))
        {
            var summaryLayout = CreateTextLayout(lane.Summary!, 11, FontWeight.Medium, Brushes.Gainsboro, TextAlignment.Left, labelRect.Width - 12);
            summaryLayout.Draw(context, labelRect.TopLeft + new Vector(8, titleLayout.Height + 4));
        }

        if (!string.IsNullOrWhiteSpace(lane.Definition.Description))
        {
            var descriptionLayout = CreateTextLayout(lane.Definition.Description!, 10, FontWeight.Normal, Brushes.Gray, TextAlignment.Left, labelRect.Width - 12);
            var y = labelRect.Bottom - descriptionLayout.Height - 2;
            if (y < labelRect.Top + titleLayout.Height + 6)
            {
                y = labelRect.Top + titleLayout.Height + 6;
            }

            descriptionLayout.Draw(context, new Point(labelRect.Left + 8, y));
        }
    }

    private void DrawSegments(DrawingContext context, LaneRenderInfo lane, Rect laneRect, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        if (lane.Fill is null || lane.Pen is null)
        {
            return;
        }

        if (lane.Segments.Count == 0)
        {
            var emptyLayout = CreateTextLayout("no samples", 11, FontWeight.Normal, Brushes.Gray, TextAlignment.Left, laneRect.Width - 16);
            emptyLayout.Draw(context, laneRect.TopLeft + new Vector(6, Math.Max(4, laneRect.Height / 2 - emptyLayout.Height / 2)));
            return;
        }

        foreach (var segment in lane.Segments)
        {
            var left = MapTime(segment.Start, startTime, endTime, laneRect);
            var right = MapTime(segment.End, startTime, endTime, laneRect);
            var width = Math.Max(2, right - left);
            var rect = new Rect(left, laneRect.Top + 4, width, laneRect.Height - 8);

            var opacityFactor = ComputeOpacity(segment, lane);
            if (lane.Fill is ISolidColorBrush solid)
            {
                var alpha = (byte)Math.Clamp(solid.Color.A * opacityFactor, 20, 255);
                var adjusted = Color.FromArgb(alpha, solid.Color.R, solid.Color.G, solid.Color.B);
                context.FillRectangle(new SolidColorBrush(adjusted), rect);
            }
            else
            {
                context.FillRectangle(lane.Fill, rect);
            }

            context.DrawRectangle(lane.Pen, rect);

            if (rect.Width > 50)
            {
                var text = segment.Label;
                var textLayout = CreateTextLayout(text, 11, FontWeight.Medium, Brushes.White, TextAlignment.Center, rect.Width - 8);
                var position = new Point(rect.Left + (rect.Width - textLayout.WidthIncludingTrailingWhitespace) / 2, rect.Top + (rect.Height - textLayout.Height) / 2);
                textLayout.Draw(context, position);
            }
        }
    }

    private static double ComputeOpacity(FlameSegment segment, LaneRenderInfo lane)
    {
        if (lane.MetricSeries is not null)
        {
            var max = lane.MetricSeries.Maximum;
            if (max <= 0)
            {
                return 0.6;
            }

            return Math.Clamp(segment.DurationMilliseconds / Math.Max(1e-3, max), 0.25, 1);
        }

        if (lane.ActivitySeries is not null)
        {
            var max = lane.ActivitySeries.MaximumDurationMs;
            if (max <= 0)
            {
                return 0.6;
            }

            return Math.Clamp(segment.DurationMilliseconds / Math.Max(1e-3, max), 0.25, 1);
        }

        return 0.6;
    }

    private void DrawTimeGrid(DrawingContext context, double labelWidth, double topPadding, double plotWidth, double plotHeight)
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

    private void DrawTimeAxis(DrawingContext context, double labelWidth, double topPadding, double plotWidth, double plotHeight, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var steps = 6;
        var duration = Math.Max(0.001, (endTime - startTime).TotalSeconds);
        for (var i = 0; i <= steps; i++)
        {
            var progress = (double)i / steps;
            var timestamp = startTime + TimeSpan.FromSeconds(progress * duration);
            var label = timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

            var x = labelWidth + progress * plotWidth;
            var textLayout = CreateTextLayout(label, 11, FontWeight.Normal, Brushes.Gray, TextAlignment.Center, 120);
            var position = new Point(x - textLayout.WidthIncludingTrailingWhitespace / 2, topPadding + plotHeight + 8);
            textLayout.Draw(context, position);
        }
    }

    private void DrawEmptyState(DrawingContext context, Rect bounds, string message)
    {
        var layout = CreateTextLayout(message, 14, FontWeight.Medium, Brushes.Gray, TextAlignment.Center, bounds.Width);
        var location = new Point((bounds.Width - layout.WidthIncludingTrailingWhitespace) / 2, (bounds.Height - layout.Height) / 2);
        layout.Draw(context, location);
    }

    private static double MapTime(DateTimeOffset timestamp, DateTimeOffset startTime, DateTimeOffset endTime, Rect laneRect)
    {
        var totalSeconds = Math.Max(0.001, (endTime - startTime).TotalSeconds);
        var elapsed = (timestamp - startTime).TotalSeconds;
        var progress = Math.Clamp(elapsed / totalSeconds, 0, 1);
        return laneRect.Left + progress * laneRect.Width;
    }

    private static TextLayout CreateTextLayout(string text, double fontSize, FontWeight weight, IBrush brush, TextAlignment alignment, double maxWidth)
    {
        var typeface = new Typeface("Inter", FontStyle.Normal, weight);
        var constraint = double.IsFinite(maxWidth) && maxWidth > 0 ? maxWidth : double.PositiveInfinity;
        return new TextLayout(text, typeface, fontSize, brush, alignment, TextWrapping.NoWrap, textTrimming: null, textDecorations: null, flowDirection: FlowDirection.LeftToRight, maxWidth: constraint);
    }

    private sealed record FlameSegment(DateTimeOffset Start, DateTimeOffset End, double DurationMilliseconds, string Label);

    private sealed class LaneRenderInfo
    {
        public LaneRenderInfo(FlameLaneDefinition definition)
        {
            Definition = definition;
            Segments = new List<FlameSegment>();
        }

        public FlameLaneDefinition Definition { get; }

        public TimelineSeries? MetricSeries { get; init; }

        public ActivitySeries? ActivitySeries { get; init; }

        public List<FlameSegment> Segments { get; }

        public IBrush? Fill { get; init; }

        public IBrush? Stroke { get; init; }

        public Pen? Pen { get; init; }

        public string? Summary { get; init; }
    }
}
