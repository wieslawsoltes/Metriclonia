using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Metriclonia.Monitor.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Metriclonia.Monitor.Visualization;

public sealed class MasterFlameChartControl : Control
{
    private static readonly ILogger Logger = Log.For<MasterFlameChartControl>();

    public static readonly StyledProperty<IEnumerable<TimelineSeries>?> SeriesProperty =
        AvaloniaProperty.Register<MasterFlameChartControl, IEnumerable<TimelineSeries>?>(nameof(Series));

    public static readonly StyledProperty<IEnumerable<ActivitySeries>?> ActivitiesProperty =
        AvaloniaProperty.Register<MasterFlameChartControl, IEnumerable<ActivitySeries>?>(nameof(Activities));

    public static readonly StyledProperty<double> VisibleDurationSecondsProperty =
        AvaloniaProperty.Register<MasterFlameChartControl, double>(nameof(VisibleDurationSeconds), 30d);

    private static readonly Dictionary<string, CategoryDefinition> Categories;
    private static readonly Dictionary<string, string> MetricCategoryLookup;
    private static readonly Dictionary<string, string> ActivityCategoryLookup;

    private readonly HashSet<TimelineSeries> _metricSubscriptions = new();
    private readonly HashSet<ActivitySeries> _activitySubscriptions = new();
    private readonly Dictionary<ActivitySeries, INotifyCollectionChanged?> _activityPointSubscriptions = new();
    private INotifyCollectionChanged? _seriesCollectionSubscription;
    private INotifyCollectionChanged? _activityCollectionSubscription;

    static MasterFlameChartControl()
    {
        Categories = BuildCategories();
        MetricCategoryLookup = BuildMetricCategoryLookup();
        ActivityCategoryLookup = BuildActivityCategoryLookup();

        AffectsRender<MasterFlameChartControl>(SeriesProperty, ActivitiesProperty, VisibleDurationSecondsProperty);
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
        else if (change.Property == VisibleDurationSecondsProperty)
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
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(245, 10, 14, 20)), bounds);

        var seriesList = Series?.Where(static s => s.IsVisible).ToList() ?? new List<TimelineSeries>();
        var activityList = Activities?.ToList() ?? new List<ActivitySeries>();

        if (seriesList.Count == 0 && activityList.Count == 0)
        {
            DrawEmptyState(context, bounds, "Waiting for metrics and activities...");
            return;
        }

        var visibleDuration = Math.Max(1, VisibleDurationSeconds);
        var latestSample = GetLatestTimestamp(seriesList, activityList);
        var endTime = latestSample != default ? latestSample : DateTimeOffset.UtcNow;
        var startTime = endTime - TimeSpan.FromSeconds(visibleDuration);

        var frames = BuildFrames(seriesList, activityList, startTime, endTime, out var rowLabels);
        if (frames.Count == 0 && rowLabels.Count == 0)
        {
            DrawEmptyState(context, bounds, "No samples in visible window");
            return;
        }

        var maxFrameDepth = frames.Count > 0 ? frames.Max(static f => f.Depth) : -1;
        var maxLabelDepth = rowLabels.Count > 0 ? rowLabels.Keys.Max() : -1;
        var totalRows = Math.Max(1, Math.Max(maxFrameDepth, maxLabelDepth) + 1);

        const double topPadding = 32;
        const double bottomPadding = 48;
        const double rowSpacing = 6;
        const double sidePadding = 12;

        var chartHeight = Math.Max(40, bounds.Height - topPadding - bottomPadding);
        var rowHeight = (chartHeight - rowSpacing * Math.Max(0, totalRows - 1)) / totalRows;
        if (rowHeight < 18)
        {
            rowHeight = 18;
        }

        var labelColumnWidth = 220;
        var plotWidth = Math.Max(40, bounds.Width - sidePadding * 2 - labelColumnWidth);
        var chartOrigin = new Point(sidePadding + labelColumnWidth, topPadding);
        var chartRect = new Rect(chartOrigin, new Size(plotWidth, rowHeight * totalRows + rowSpacing * Math.Max(0, totalRows - 1)));

        DrawTimeGrid(context, chartRect, totalRows, rowHeight, rowSpacing);

        using (context.PushClip(chartRect))
        {
            for (var depth = 0; depth < totalRows; depth++)
            {
                var rowTop = chartRect.Top + depth * (rowHeight + rowSpacing);
                var rowRect = new Rect(chartRect.Left, rowTop, chartRect.Width, rowHeight);
                DrawRowBackground(context, rowRect, depth);
            }

            foreach (var frame in frames.OrderBy(static f => f.Depth).ThenBy(static f => f.Start))
            {
                DrawFrame(context, frame, chartRect, rowHeight, rowSpacing, startTime, endTime);
            }
        }

        DrawRowLabels(context, rowLabels, chartRect, rowHeight, rowSpacing, labelColumnWidth, sidePadding);

        DrawTimeAxis(context, chartRect, startTime, endTime);

        var title = CreateTextLayout("Master Flame Graph", 18, FontWeight.SemiBold, Brushes.Gainsboro, TextAlignment.Left, plotWidth + labelColumnWidth);
        title.Draw(context, new Point(sidePadding, 8));
    }

    private static DateTimeOffset GetLatestTimestamp(IReadOnlyList<TimelineSeries> seriesList, IReadOnlyList<ActivitySeries> activityList)
    {
        var latest = DateTimeOffset.MinValue;

        foreach (var series in seriesList)
        {
            if (series.Points.Count == 0)
            {
                continue;
            }

            var ts = series.Points[^1].Timestamp;
            if (ts > latest)
            {
                latest = ts;
            }
        }

        foreach (var activity in activityList)
        {
            if (activity.Points.Count == 0)
            {
                continue;
            }

            var point = activity.Points[^1];
            var end = point.Timestamp + TimeSpan.FromMilliseconds(point.DurationMilliseconds);
            if (end > latest)
            {
                latest = end;
            }
        }

        return latest;
    }

    private List<Frame> BuildFrames(IReadOnlyList<TimelineSeries> seriesList, IReadOnlyList<ActivitySeries> activityList, DateTimeOffset startTime, DateTimeOffset endTime, out Dictionary<int, string> rowLabels)
    {
        var frames = new List<Frame>();
        rowLabels = new Dictionary<int, string>();

        var metricPlacements = PrepareMetricPlacements(seriesList);
        var nextLeafDepth = metricPlacements.Count > 0 ? metricPlacements[^1].Depth + 1 : 0;
        var activityPlacements = PrepareActivityPlacements(activityList, nextLeafDepth);

        foreach (var placement in metricPlacements)
        {
            var series = placement.Series;
            rowLabels[placement.Depth] = series.DisplayName;
            if (series.Points.Count == 0)
            {
                continue;
            }

            var categoryKey = placement.CategoryKey;
            if (!Categories.ContainsKey(categoryKey))
            {
                continue;
            }

            var leafDepth = placement.Depth;
            var fill = new SolidColorBrush(Color.FromArgb(160, series.Color.R, series.Color.G, series.Color.B)).ToImmutable();
            var pen = new Pen(series.Stroke, 1);

            for (var i = series.Points.Count - 1; i >= 0; i--)
            {
                var point = series.Points[i];
                if (!double.IsFinite(point.Value) || point.Value <= 0)
                {
                    continue;
                }

                var end = point.Timestamp;
                var start = end - TimeSpan.FromMilliseconds(point.Value);

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

                var durationMs = (clampedEnd - clampedStart).TotalMilliseconds;
                var label = $"{series.DisplayName} {point.Value:0.###} {series.Unit}".Trim();

                frames.Add(new Frame(clampedStart, clampedEnd, durationMs, leafDepth, fill, pen, label));
            }
        }

        foreach (var placement in activityPlacements)
        {
            var activity = placement.Series;
            rowLabels[placement.Depth] = activity.DisplayName;
            if (activity.Points.Count == 0)
            {
                continue;
            }

            var categoryKey = placement.CategoryKey;
            if (!Categories.ContainsKey(categoryKey))
            {
                continue;
            }

            var leafDepth = placement.Depth;
            var fill = new SolidColorBrush(Color.FromArgb(150, activity.Color.R, activity.Color.G, activity.Color.B)).ToImmutable();
            var pen = new Pen(activity.Stroke, 1);

            for (var i = activity.Points.Count - 1; i >= 0; i--)
            {
                var point = activity.Points[i];
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

                var durationMs = (clampedEnd - clampedStart).TotalMilliseconds;
                var label = $"{activity.DisplayName} {point.DurationMilliseconds:0.###} ms";

                frames.Add(new Frame(clampedStart, clampedEnd, durationMs, leafDepth, fill, pen, label));
            }
        }

        return frames;
    }

    private static List<MetricPlacement> PrepareMetricPlacements(IReadOnlyList<TimelineSeries> seriesList)
    {
        var placements = new List<MetricPlacement>(seriesList.Count);
        if (seriesList.Count == 0)
        {
            return placements;
        }

        var descriptors = seriesList
            .Select(series =>
            {
                var categoryKey = ResolveMetricCategory(series);
                var categoryDepth = Categories.TryGetValue(categoryKey, out var category) ? category.Depth : int.MaxValue;
                return new
                {
                    Series = series,
                    CategoryKey = categoryKey,
                    CategoryDepth = categoryDepth
                };
            })
            .OrderBy(item => item.CategoryDepth)
            .ThenBy(item => item.CategoryKey, StringComparer.Ordinal)
            .ThenBy(item => item.Series.DisplayName, StringComparer.Ordinal)
            .ThenBy(item => item.Series.InstrumentName, StringComparer.Ordinal)
            .ToList();

        var depth = 0;
        foreach (var descriptor in descriptors)
        {
            placements.Add(new MetricPlacement(descriptor.Series, descriptor.CategoryKey, depth++));
        }

        return placements;
    }

    private static List<ActivityPlacement> PrepareActivityPlacements(IReadOnlyList<ActivitySeries> activityList, int startingDepth)
    {
        var placements = new List<ActivityPlacement>(activityList.Count);
        if (activityList.Count == 0)
        {
            return placements;
        }

        var descriptors = activityList
            .Select(activity =>
            {
                var categoryKey = ResolveActivityCategory(activity);
                var categoryDepth = Categories.TryGetValue(categoryKey, out var category) ? category.Depth : int.MaxValue;
                return new
                {
                    Activity = activity,
                    CategoryKey = categoryKey,
                    CategoryDepth = categoryDepth
                };
            })
            .OrderBy(item => item.CategoryDepth)
            .ThenBy(item => item.CategoryKey, StringComparer.Ordinal)
            .ThenBy(item => item.Activity.DisplayName, StringComparer.Ordinal)
            .ThenBy(item => item.Activity.Name, StringComparer.Ordinal)
            .ToList();

        var depth = startingDepth;
        foreach (var descriptor in descriptors)
        {
            placements.Add(new ActivityPlacement(descriptor.Activity, descriptor.CategoryKey, depth++));
        }

        return placements;
    }

    private static string ResolveMetricCategory(TimelineSeries series)
    {
        if (MetricCategoryLookup.TryGetValue(series.InstrumentName, out var category))
        {
            return category;
        }

        if (!string.IsNullOrWhiteSpace(series.MeterName) && MetricCategoryLookup.TryGetValue(series.MeterName + "::" + series.InstrumentName, out category))
        {
            return category;
        }

        return "metrics.other";
    }

    private static string ResolveActivityCategory(ActivitySeries series)
    {
        if (ActivityCategoryLookup.TryGetValue(series.Name, out var category))
        {
            return category;
        }

        return "activities.other";
    }

    private void DrawFrame(DrawingContext context, Frame frame, Rect chartRect, double rowHeight, double rowSpacing, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var rowTop = chartRect.Top + frame.Depth * (rowHeight + rowSpacing);
        var rowRect = new Rect(chartRect.Left, rowTop, chartRect.Width, rowHeight);

        if (rowRect.Bottom < chartRect.Top || rowRect.Top > chartRect.Bottom)
        {
            return;
        }

        var left = MapTime(frame.Start, startTime, endTime, chartRect);
        var right = MapTime(frame.End, startTime, endTime, chartRect);
        if (double.IsNaN(left) || double.IsNaN(right))
        {
            return;
        }

        var width = right - left;
        var rect = new Rect(left, rowTop + 2, Math.Max(1, width), rowHeight - 4);
        context.FillRectangle(frame.Fill, rect);
        context.DrawRectangle(frame.Pen, rect);

        if (width > 60)
        {
            var layout = CreateTextLayout(frame.Label, 11, FontWeight.Medium, Brushes.White, TextAlignment.Center, rect.Width - 8);
            var position = new Point(rect.Left + (rect.Width - layout.WidthIncludingTrailingWhitespace) / 2, rect.Top + (rect.Height - layout.Height) / 2);
            layout.Draw(context, position);
        }
    }

    private void DrawRowLabels(DrawingContext context, IReadOnlyDictionary<int, string> rowLabels, Rect chartRect, double rowHeight, double rowSpacing, double labelColumnWidth, double sidePadding)
    {
        if (rowLabels.Count == 0)
        {
            return;
        }

        foreach (var (depth, label) in rowLabels.OrderBy(static kvp => kvp.Key))
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var rowTop = chartRect.Top + depth * (rowHeight + rowSpacing);
            var labelRect = new Rect(sidePadding, rowTop, labelColumnWidth - 8, rowHeight);
            var background = depth % 2 == 0 ? Color.FromArgb(32, 255, 255, 255) : Color.FromArgb(16, 255, 255, 255);
            context.FillRectangle(new SolidColorBrush(background), labelRect);

            var layout = CreateTextLayout(label, 12, FontWeight.SemiBold, Brushes.Gainsboro, TextAlignment.Left, labelRect.Width - 12);
            var textPosition = new Point(labelRect.Left + 8, labelRect.Top + Math.Max(2, (labelRect.Height - layout.Height) / 2));
            layout.Draw(context, textPosition);
        }

        var divider = new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1);
        var dividerStart = new Point(sidePadding + labelColumnWidth - 4, chartRect.Top);
        var dividerEnd = new Point(sidePadding + labelColumnWidth - 4, chartRect.Bottom);
        context.DrawLine(divider, dividerStart, dividerEnd);
    }

    private static double MapTime(DateTimeOffset timestamp, DateTimeOffset startTime, DateTimeOffset endTime, Rect chartRect)
    {
        var totalSeconds = Math.Max(0.001, (endTime - startTime).TotalSeconds);
        var elapsed = (timestamp - startTime).TotalSeconds;
        var progress = Math.Clamp(elapsed / totalSeconds, 0, 1);
        return chartRect.Left + progress * chartRect.Width;
    }

    private static void DrawRowBackground(DrawingContext context, Rect rowRect, int depth)
    {
        var alpha = depth % 2 == 0 ? (byte)22 : (byte)12;
        var color = Color.FromArgb(alpha, 255, 255, 255);
        context.FillRectangle(new SolidColorBrush(color), rowRect);
    }

    private static void DrawTimeGrid(DrawingContext context, Rect chartRect, int rows, double rowHeight, double rowSpacing)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), 1);
        const int steps = 6;

        for (var i = 0; i <= steps; i++)
        {
            var progress = (double)i / steps;
            var x = chartRect.Left + progress * chartRect.Width;
            context.DrawLine(pen, new Point(x, chartRect.Top), new Point(x, chartRect.Bottom));
        }

        for (var depth = 0; depth <= rows; depth++)
        {
            var y = chartRect.Top + depth * (rowHeight + rowSpacing) - rowSpacing / 2;
            if (y < chartRect.Top || y > chartRect.Bottom)
            {
                continue;
            }

            context.DrawLine(pen, new Point(chartRect.Left, y), new Point(chartRect.Right, y));
        }
    }

    private static void DrawTimeAxis(DrawingContext context, Rect chartRect, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        const int steps = 6;
        var duration = Math.Max(0.001, (endTime - startTime).TotalSeconds);

        for (var i = 0; i <= steps; i++)
        {
            var progress = (double)i / steps;
            var timestamp = startTime + TimeSpan.FromSeconds(progress * duration);
            var label = timestamp.ToLocalTime().ToString("HH:mm:ss.fff");

            var x = chartRect.Left + progress * chartRect.Width;
            var textLayout = CreateTextLayout(label, 11, FontWeight.Normal, Brushes.Gray, TextAlignment.Center, 120);
            var position = new Point(x - textLayout.WidthIncludingTrailingWhitespace / 2, chartRect.Bottom + 8);
            textLayout.Draw(context, position);
        }
    }

    private static void DrawEmptyState(DrawingContext context, Rect bounds, string message)
    {
        var layout = CreateTextLayout(message, 14, FontWeight.Medium, Brushes.Gray, TextAlignment.Center, bounds.Width);
        var location = new Point((bounds.Width - layout.WidthIncludingTrailingWhitespace) / 2, (bounds.Height - layout.Height) / 2);
        layout.Draw(context, location);
    }

    private static TextLayout CreateTextLayout(string text, double fontSize, FontWeight weight, IBrush brush, TextAlignment alignment, double maxWidth)
    {
        var typeface = new Typeface("Inter", FontStyle.Normal, weight);
        var constraint = double.IsFinite(maxWidth) && maxWidth > 0 ? maxWidth : double.PositiveInfinity;
        return new TextLayout(text, typeface, fontSize, brush, alignment, TextWrapping.NoWrap, textTrimming: null, textDecorations: null, flowDirection: FlowDirection.LeftToRight, maxWidth: constraint);
    }

    private static Dictionary<string, CategoryDefinition> BuildCategories()
    {
        var categories = new List<CategoryDefinition>
        {
            new("root", "All activity", null, Color.FromArgb(30, 255, 255, 255)),
            new("metrics", "Metrics", "root", Color.FromArgb(65, 66, 135, 245)),
            new("metrics.rendering", "Rendering", "metrics", Color.FromArgb(95, 96, 186, 255)),
            new("metrics.layout", "Layout", "metrics", Color.FromArgb(95, 126, 206, 255)),
            new("metrics.input", "Input", "metrics", Color.FromArgb(95, 166, 206, 255)),
            new("metrics.resources", "Resources", "metrics", Color.FromArgb(95, 196, 206, 255)),
            new("metrics.other", "Other metrics", "metrics", Color.FromArgb(80, 90, 90, 120)),
            new("activities", "Activities", "root", Color.FromArgb(70, 180, 110, 240)),
            new("activities.layout", "Layout activities", "activities", Color.FromArgb(95, 140, 180, 255)),
            new("activities.input", "Input activities", "activities", Color.FromArgb(95, 120, 200, 220)),
            new("activities.styling", "Style & resources", "activities", Color.FromArgb(95, 200, 130, 220)),
            new("activities.other", "Other activities", "activities", Color.FromArgb(80, 120, 80, 120))
        };

        var lookup = new Dictionary<string, CategoryDefinition>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            lookup[category.Key] = category.WithDepth(CalculateDepth(category, lookup));
        }

        return lookup;
    }

    private static int CalculateDepth(CategoryDefinition category, IReadOnlyDictionary<string, CategoryDefinition> lookup)
    {
        var depth = 0;
        var current = category.ParentKey;
        while (!string.IsNullOrEmpty(current))
        {
            if (!lookup.TryGetValue(current, out var parent))
            {
                break;
            }

            depth++;
            current = parent.ParentKey;
        }

        return depth;
    }

    private static Dictionary<string, string> BuildMetricCategoryLookup()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["avalonia.comp.render.time"] = "metrics.rendering",
            ["avalonia.comp.update.time"] = "metrics.rendering",
            ["avalonia.ui.render.time"] = "metrics.rendering",
            ["avalonia.ui.measure.time"] = "metrics.layout",
            ["avalonia.ui.arrange.time"] = "metrics.layout",
            ["avalonia.ui.input.time"] = "metrics.input",
            ["avalonia.ui.resource.lookup.time"] = "metrics.resources",
            ["avalonia.ui.render.pass.time"] = "metrics.rendering"
        };
    }

    private static Dictionary<string, string> BuildActivityCategoryLookup()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Avalonia.MeasuringLayoutable"] = "activities.layout",
            ["Avalonia.ArrangingLayoutable"] = "activities.layout",
            ["Avalonia.PerformingHitTest"] = "activities.input",
            ["Avalonia.RaisingRoutedEvent"] = "activities.input",
            ["Avalonia.AttachingStyle"] = "activities.styling",
            ["Avalonia.EvaluatingStyle"] = "activities.styling",
            ["Avalonia.FindingResource"] = "activities.styling"
        };
    }

    private sealed record MetricPlacement(TimelineSeries Series, string CategoryKey, int Depth);

    private sealed record ActivityPlacement(ActivitySeries Series, string CategoryKey, int Depth);

    private sealed record CategoryDefinition
    {
        public CategoryDefinition(string Key, string DisplayName, string? ParentKey, Color color)
        {
            this.Key = Key;
            this.DisplayName = DisplayName;
            this.ParentKey = ParentKey;
            Color = color;
            Brush = new ImmutableSolidColorBrush(Color);
            var alpha = (byte)Math.Clamp(Color.A + 40, 40, 255);
            BorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(alpha, Color.R, Color.G, Color.B));
        }

        public string Key { get; }

        public string DisplayName { get; }

        public string? ParentKey { get; }

        public Color Color { get; }

        public IBrush Brush { get; }

        public IBrush BorderBrush { get; }

        public int Depth { get; private init; }

        public CategoryDefinition WithDepth(int depth)
            => this with { Depth = depth };
    }

    private sealed class Frame
    {
        public Frame(DateTimeOffset start, DateTimeOffset end, double durationMs, int depth, IBrush fill, Pen pen, string label)
        {
            Start = start;
            End = end;
            DurationMs = durationMs;
            Depth = depth;
            Fill = fill;
            Pen = pen;
            Label = label;
        }

        public DateTimeOffset Start { get; }

        public DateTimeOffset End { get; }

        public double DurationMs { get; }

        public int Depth { get; }

        public IBrush Fill { get; }

        public Pen Pen { get; }

        public string Label { get; }
    }
}
