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

public class TimelinePlotControl : Control
{
    private static readonly ILogger Logger = Log.For<TimelinePlotControl>();

    public static readonly StyledProperty<IEnumerable<TimelineSeries>?> SeriesProperty =
        AvaloniaProperty.Register<TimelinePlotControl, IEnumerable<TimelineSeries>?>(nameof(Series));

    public static readonly StyledProperty<double> VisibleDurationSecondsProperty =
        AvaloniaProperty.Register<TimelinePlotControl, double>(nameof(VisibleDurationSeconds), 30d);

    private readonly HashSet<TimelineSeries> _attachedSeries = new();

    static TimelinePlotControl()
    {
        AffectsRender<TimelinePlotControl>(SeriesProperty, VisibleDurationSecondsProperty);
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

    public override void Render(DrawingContext context)
    {
        Logger.LogTrace("Render pass bounds={Bounds}", Bounds);
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(240, 12, 16, 24)), bounds);

        var seriesList = Series?.Where(s => s.IsVisible).ToList();
        if (seriesList is null || seriesList.Count == 0)
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

        var visibleDuration = Math.Max(1, VisibleDurationSeconds);
        var latestSample = seriesList.Max(static s => s.LastTimestamp);
        var endTime = latestSample != default ? latestSample : DateTimeOffset.UtcNow;
        var startTime = endTime - TimeSpan.FromSeconds(visibleDuration);

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

        DrawTimeAxis(context, labelWidth, topPadding, plotWidth, plotHeight, startTime, endTime);
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
            Logger.LogTrace("Renderer skipped {Display} because all {Total} points were non-finite", series.DisplayName, total);
            return;
        }

        if (!TryBuildRenderPoints(snapshot, startTime, endTime, series.RenderMode, out var renderPoints, out var statsPoints))
        {
            Logger.LogTrace("Renderer skipped {Display}: no points in visible window (total={Total}, finite={Finite})", series.DisplayName, total, finite);
            return;
        }

        Logger.LogTrace("Renderer drawing {Display} mode={Mode} total={Total} finite={Finite} render={Render} stats={Stats}", series.DisplayName, series.RenderMode, total, finite, renderPoints.Count, statsPoints.Count);

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
        var totalSeconds = Math.Max(0.001, (endTime - startTime).TotalSeconds);
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

    private void DrawTimeAxis(DrawingContext context, double labelWidth, double topPadding, double plotWidth, double plotHeight, DateTimeOffset startTime, DateTimeOffset endTime)
    {
        var steps = 6;
        var duration = Math.Max(0.001, (endTime - startTime).TotalSeconds);
        for (var i = 0; i <= steps; i++)
        {
            var progress = (double)i / steps;
            var timestamp = startTime + TimeSpan.FromSeconds(progress * duration);
            var label = timestamp.ToLocalTime().ToString("HH:mm:ss");

            var x = labelWidth + progress * plotWidth;
            var textLayout = CreateTextLayout(label, 11, FontWeight.Normal, Brushes.Gray, TextAlignment.Center, 80);
            var position = new Point(x - textLayout.WidthIncludingTrailingWhitespace / 2, topPadding + plotHeight + 6);
            textLayout.Draw(context, position);
        }
    }

    private static TextLayout CreateTextLayout(string text, double fontSize, FontWeight weight, IBrush brush, TextAlignment alignment, double maxWidth)
    {
        var typeface = new Typeface("Inter", FontStyle.Normal, weight);
        var constraint = double.IsFinite(maxWidth) && maxWidth > 0 ? maxWidth : double.PositiveInfinity;
        return new TextLayout(text, typeface, fontSize, brush, alignment, TextWrapping.NoWrap, textTrimming: null, textDecorations: null, flowDirection: FlowDirection.LeftToRight, maxWidth: constraint);
    }
}
