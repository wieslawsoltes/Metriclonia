using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Metriclonia.Monitor.Visualization;

public sealed class ActivityGraphControl : Control
{
    public static readonly StyledProperty<IEnumerable<ActivityPoint>?> PointsProperty =
        AvaloniaProperty.Register<ActivityGraphControl, IEnumerable<ActivityPoint>?>(nameof(Points));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<ActivityGraphControl, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<ActivityGraphControl, IBrush?>(nameof(GridBrush));

    private INotifyCollectionChanged? _trackedCollection;

    static ActivityGraphControl()
    {
        AffectsRender<ActivityGraphControl>(PointsProperty, LineBrushProperty, GridBrushProperty);
    }

    public IEnumerable<ActivityPoint>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PointsProperty)
        {
            if (_trackedCollection is not null)
            {
                _trackedCollection.CollectionChanged -= OnPointsCollectionChanged;
                _trackedCollection = null;
            }

            if (change.NewValue is INotifyCollectionChanged notify)
            {
                _trackedCollection = notify;
                notify.CollectionChanged += OnPointsCollectionChanged;
            }
        }
    }

    private void OnPointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), bounds);

        var list = Points?.ToList();
        if (list is null || list.Count == 0)
        {
            DrawPlaceholder(context, bounds, "Awaiting data");
            return;
        }

        if (list.Count == 1)
        {
            DrawSinglePoint(context, bounds, list[0]);
            return;
        }

        var minTimestamp = list.Min(p => p.Timestamp);
        var maxTimestamp = list.Max(p => p.Timestamp);
        var totalSeconds = Math.Max(0.001, (maxTimestamp - minTimestamp).TotalSeconds);
        if (totalSeconds < 0.001)
        {
            // Fallback to index positions if timestamps identical.
            totalSeconds = list.Count - 1;
        }

        var minDuration = list.Min(p => p.DurationMilliseconds);
        var maxDuration = list.Max(p => p.DurationMilliseconds);
        if (Math.Abs(maxDuration - minDuration) < 0.0001)
        {
            maxDuration = minDuration + 1;
            minDuration = minDuration - 0.5;
        }

        var padding = new Thickness(16, 12, 16, 24);
        var plotRect = new Rect(bounds.Position + new Point(padding.Left, padding.Top),
            new Size(Math.Max(10, bounds.Width - padding.Left - padding.Right), Math.Max(10, bounds.Height - padding.Top - padding.Bottom)));

        DrawGrid(context, plotRect, minDuration, maxDuration);
        DrawLine(context, plotRect, list, minTimestamp, totalSeconds, minDuration, maxDuration);
        DrawAxis(context, plotRect, minTimestamp, maxTimestamp);
    }

    private void DrawPlaceholder(DrawingContext context, Rect bounds, string text)
    {
        var layout = CreateTextLayout(text, 12, FontWeight.Medium, Brushes.Gray, bounds.Width - 24);
        var origin = new Point(bounds.Center.X - layout.WidthIncludingTrailingWhitespace / 2, bounds.Center.Y - layout.Height / 2);
        layout.Draw(context, origin);
    }

    private void DrawSinglePoint(DrawingContext context, Rect bounds, ActivityPoint point)
    {
        DrawPlaceholder(context, bounds, $"{point.DurationMilliseconds:0.000} ms at {point.Timestamp:HH:mm:ss.fff}");
    }

    private void DrawGrid(DrawingContext context, Rect rect, double minDuration, double maxDuration)
    {
        var brush = GridBrush ?? new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        var pen = new Pen(brush, 1);

        const int horizontalLines = 4;
        for (var i = 0; i <= horizontalLines; i++)
        {
            var y = rect.Top + rect.Height * i / horizontalLines;
            context.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));

            var value = maxDuration - (maxDuration - minDuration) * i / horizontalLines;
            var label = CreateTextLayout($"{value:0.000} ms", 10, FontWeight.Normal, Brushes.Gray, 80);
            label.Draw(context, new Point(rect.Left + 4, y - label.Height - 2));
        }
    }

    private void DrawLine(DrawingContext context, Rect rect, IList<ActivityPoint> points, DateTimeOffset minTimestamp, double totalSeconds, double minDuration, double maxDuration)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var point = points[i];
                var x = rect.Left + CalculateX(minTimestamp, totalSeconds, points.Count, i, point.Timestamp) * rect.Width;
                var y = rect.Bottom - (point.DurationMilliseconds - minDuration) / (maxDuration - minDuration) * rect.Height;
                var current = new Point(x, y);

                if (i == 0)
                {
                    ctx.BeginFigure(current, false);
                }
                else
                {
                    ctx.LineTo(current);
                }
            }
        }

        var lineBrush = LineBrush ?? Brushes.DeepSkyBlue;
        var pen = new Pen(lineBrush, 2) { LineJoin = PenLineJoin.Round };
        context.DrawGeometry(null, pen, geometry);

        var lastPoint = points[^1];
        var lastX = rect.Left + CalculateX(minTimestamp, totalSeconds, points.Count, points.Count - 1, lastPoint.Timestamp) * rect.Width;
        var lastY = rect.Bottom - (lastPoint.DurationMilliseconds - minDuration) / (maxDuration - minDuration) * rect.Height;
        context.DrawEllipse(lineBrush, null, new Point(lastX, lastY), 4, 4);
    }

    private static double CalculateX(DateTimeOffset minTimestamp, double totalSeconds, int count, int index, DateTimeOffset timestamp)
    {
        if (totalSeconds <= 0)
        {
            return count <= 1 ? 0 : (double)index / (count - 1);
        }

        var elapsed = (timestamp - minTimestamp).TotalSeconds;
        if (double.IsNaN(elapsed) || double.IsInfinity(elapsed))
        {
            return count <= 1 ? 0 : (double)index / (count - 1);
        }

        return Math.Clamp(elapsed / totalSeconds, 0, 1);
    }

    private void DrawAxis(DrawingContext context, Rect rect, DateTimeOffset minTimestamp, DateTimeOffset maxTimestamp)
    {
        var brush = Brushes.Gray;
        const int segments = 4;
        var totalSeconds = Math.Max(0.001, (maxTimestamp - minTimestamp).TotalSeconds);

        for (var i = 0; i <= segments; i++)
        {
            var progress = (double)i / segments;
            var x = rect.Left + progress * rect.Width;
            var timestamp = minTimestamp + TimeSpan.FromSeconds(progress * totalSeconds);
            var label = CreateTextLayout(timestamp.ToLocalTime().ToString("HH:mm:ss"), 10, FontWeight.Normal, brush, 80);
            label.Draw(context, new Point(x - label.WidthIncludingTrailingWhitespace / 2, rect.Bottom + 4));
        }
    }

    private static TextLayout CreateTextLayout(string text, double fontSize, FontWeight weight, IBrush brush, double maxWidth)
    {
        var typeface = new Typeface("Inter", FontStyle.Normal, weight);
        return new TextLayout(text, typeface, fontSize, brush, TextAlignment.Left, TextWrapping.NoWrap, maxWidth: maxWidth);
    }
}
