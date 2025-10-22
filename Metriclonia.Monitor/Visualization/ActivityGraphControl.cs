using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Metriclonia.Monitor.Visualization;

public sealed class ActivityGraphControl : Control
{
    private static readonly ImmutableSolidColorBrush BackgroundBrush = new(Color.FromArgb(30, 255, 255, 255));
    private static readonly ImmutablePen DefaultGridPen = new(new ImmutableSolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
    private static readonly ImmutableSolidColorBrush DefaultLineBrush = new(Colors.DeepSkyBlue);
    private static readonly Typeface LabelTypeface = new("Inter", FontStyle.Normal, FontWeight.Normal);
    private static readonly Typeface LabelTypefaceBold = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private static readonly TextLayout PlaceholderLayout = CreateStaticLayout("Awaiting data", 12, FontWeight.Medium, Brushes.Gray, TextAlignment.Center);
    private static readonly ImmutableSolidColorBrush StatLabelBackground = new(Color.FromArgb(120, 12, 16, 24));

    private CachedLayout[] _gridLabelCache = { new(), new(), new(), new(), new() };
    private CachedLayout[] _axisLabelCache = { new(), new(), new(), new(), new() };
    private CachedLayout _singlePointLayout = new();
    private CachedLayout _minStatLayout = new();
    private CachedLayout _avgStatLayout = new();
    private CachedLayout _maxStatLayout = new();

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
        context.FillRectangle(BackgroundBrush, bounds);

        var source = Points;
        if (source is null)
        {
            DrawPlaceholder(context, bounds, PlaceholderLayout);
            return;
        }

        IReadOnlyList<ActivityPoint> pointsList;
        if (source is IReadOnlyList<ActivityPoint> readOnly)
        {
            pointsList = readOnly;
        }
        else
        {
            var buffer = new List<ActivityPoint>();
            foreach (var point in source)
            {
                buffer.Add(point);
            }
            pointsList = buffer;
        }

        if (pointsList.Count == 0)
        {
            DrawPlaceholder(context, bounds, PlaceholderLayout);
            return;
        }

        if (pointsList.Count == 1)
        {
            DrawSinglePoint(context, bounds, pointsList[0]);
            return;
        }

        var metrics = ComputeMetrics(pointsList);
        var totalSeconds = metrics.TotalSeconds <= 0.001
            ? Math.Max(1, pointsList.Count - 1)
            : metrics.TotalSeconds;
        var minDuration = metrics.MinDuration;
        var maxDuration = metrics.MaxDuration;
        if (Math.Abs(maxDuration - minDuration) < 0.0001)
        {
            maxDuration = minDuration + 1;
            minDuration -= 0.5;
        }

        var padding = new Thickness(16, 12, 16, 24);
        var plotRect = new Rect(bounds.Position + new Point(padding.Left, padding.Top),
            new Size(Math.Max(10, bounds.Width - padding.Left - padding.Right), Math.Max(10, bounds.Height - padding.Top - padding.Bottom)));

        if (plotRect.Width <= 0 || plotRect.Height <= 0)
        {
            DrawPlaceholder(context, bounds, PlaceholderLayout);
            return;
        }

        var gridPen = ResolveGridPen(GridBrush);
        DrawGrid(context, plotRect, minDuration, maxDuration, gridPen);

        var lineBrushColor = (LineBrush as ISolidColorBrush)?.Color ?? DefaultLineBrush.Color;
        DrawStats(context, plotRect, minDuration, maxDuration, metrics.AverageDuration, lineBrushColor);

        using var builder = ActivityLineRenderBuilder.Create(plotRect, pointsList, metrics.MinTimestamp, totalSeconds, minDuration, maxDuration);
        if (builder is not null)
        {
            var lineBrush = LineBrush ?? DefaultLineBrush;
            var scheduled = false;
            if (builder.PointCount >= 2 && LineBrush is ISolidColorBrush)
            {
                var operation = builder.CreateSkiaOperation(lineBrushColor);
                if (operation is not null)
                {
                    context.Custom(operation);
                    scheduled = true;
                }
            }

            if (!scheduled)
            {
                builder.DrawFallback(context, lineBrush);
            }

            if (builder.HasLastPoint)
            {
                context.DrawEllipse(lineBrush, null, builder.LastPoint, 4, 4);
            }
        }

        DrawAxis(context, plotRect, metrics.MinTimestamp, metrics.MaxTimestamp);
    }

    private static ActivityMetrics ComputeMetrics(IReadOnlyList<ActivityPoint> points)
    {
        var first = points[0];
        var minTimestamp = first.Timestamp;
        var maxTimestamp = first.Timestamp;
        var minDuration = first.DurationMilliseconds;
        var maxDuration = first.DurationMilliseconds;
        double sumDuration = 0;

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.Timestamp < minTimestamp)
            {
                minTimestamp = point.Timestamp;
            }

            if (point.Timestamp > maxTimestamp)
            {
                maxTimestamp = point.Timestamp;
            }

            var duration = point.DurationMilliseconds;
            if (duration < minDuration)
            {
                minDuration = duration;
            }
            else if (duration > maxDuration)
            {
                maxDuration = duration;
            }

            sumDuration += duration;
        }

        var totalSeconds = (maxTimestamp - minTimestamp).TotalSeconds;
        return new ActivityMetrics(minTimestamp, maxTimestamp, minDuration, maxDuration, sumDuration / points.Count, totalSeconds);
    }

    private void DrawPlaceholder(DrawingContext context, Rect bounds, TextLayout layout)
    {
        var origin = new Point(bounds.Center.X - layout.WidthIncludingTrailingWhitespace / 2, bounds.Center.Y - layout.Height / 2);
        layout.Draw(context, origin);
    }

    private void DrawSinglePoint(DrawingContext context, Rect bounds, ActivityPoint point)
    {
        var text = $"{point.DurationMilliseconds:0.000} ms at {point.Timestamp:HH:mm:ss.fff}";
        var layout = GetOrCreateLayout(ref _singlePointLayout, text, 12, FontWeight.Medium, Brushes.Gray, TextAlignment.Center, bounds.Width - 24);
        DrawPlaceholder(context, bounds, layout);
    }

    private void DrawGrid(DrawingContext context, Rect rect, double minDuration, double maxDuration, ImmutablePen pen)
    {
        const int horizontalLines = 4;
        for (var i = 0; i <= horizontalLines; i++)
        {
            var y = rect.Top + rect.Height * i / horizontalLines;
            context.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));

            var value = maxDuration - (maxDuration - minDuration) * i / horizontalLines;
            var label = GetOrCreateLayout(ref _gridLabelCache[i], $"{value:0.000} ms", 10, FontWeight.Normal, Brushes.Gray, TextAlignment.Left, 80);
            label.Draw(context, new Point(rect.Left + 4, y - label.Height - 2));
        }
    }

    private void DrawStats(DrawingContext context, Rect rect, double min, double max, double avg, Color baseColor)
    {
        var minBrush = new SolidColorBrush(Color.FromArgb(140, baseColor.R, baseColor.G, baseColor.B));
        var avgBrush = new SolidColorBrush(baseColor);
        var maxBrush = new SolidColorBrush(Color.FromArgb(190, baseColor.R, baseColor.G, baseColor.B));

        DrawStatLine(context, rect, min, minBrush, "min", min, max, ref _minStatLayout);
        DrawStatLine(context, rect, avg, avgBrush, "avg", min, max, ref _avgStatLayout);
        DrawStatLine(context, rect, max, maxBrush, "max", min, max, ref _maxStatLayout);
    }

    private void DrawStatLine(DrawingContext context, Rect rect, double value, IBrush brush, string label, double minValue, double maxValue, ref CachedLayout cache)
    {
        if (double.IsNaN(value) || rect.Height <= 0)
        {
            return;
        }

        var range = Math.Max(0.0001, maxValue - minValue);
        var normalized = Math.Clamp((value - minValue) / range, 0, 1);
        var y = rect.Bottom - normalized * rect.Height;

        var pen = new Pen(brush, label == "avg" ? 2 : 1)
        {
            DashStyle = label == "avg" ? DashStyle.Dot : DashStyle.Dash
        };
        context.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));

        var layout = GetOrCreateLayout(ref cache, $"{label} {value:0.000} ms", 10, FontWeight.SemiBold, brush, TextAlignment.Left, 120);
        var padding = new Thickness(4, 2);
        var size = new Size(layout.WidthIncludingTrailingWhitespace + padding.Left + padding.Right, layout.Height + padding.Top + padding.Bottom);
        var origin = new Point(rect.Right - size.Width - 4, Math.Clamp(y - size.Height / 2, rect.Top, rect.Bottom - size.Height));

        context.FillRectangle(StatLabelBackground, new Rect(origin, size), 6);
        layout.Draw(context, origin + new Point(padding.Left, padding.Top));
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
            var label = GetOrCreateLayout(ref _axisLabelCache[i], timestamp.ToLocalTime().ToString("HH:mm:ss"), 10, FontWeight.Normal, brush, TextAlignment.Center, 80);
            label.Draw(context, new Point(x - label.WidthIncludingTrailingWhitespace / 2, rect.Bottom + 4));
        }
    }

    private static ImmutablePen ResolveGridPen(IBrush? brush)
    {
        if (brush is null)
        {
            return DefaultGridPen;
        }

        if (brush is ImmutableSolidColorBrush immutableSolid)
        {
            return new ImmutablePen(immutableSolid, 1);
        }

        return new ImmutablePen(brush.ToImmutable(), 1);
    }

    private static TextLayout GetOrCreateLayout(ref CachedLayout cache, string text, double fontSize, FontWeight weight, IBrush brush, TextAlignment alignment, double maxWidth)
    {
        const double WidthTolerance = 0.5;

        if (cache.Layout is not null
            && cache.Text == text
            && Math.Abs(cache.FontSize - fontSize) < double.Epsilon
            && cache.Weight == weight
            && ReferenceEquals(cache.Brush, brush)
            && cache.Alignment == alignment
            && Math.Abs(cache.MaxWidth - maxWidth) <= WidthTolerance)
        {
            return cache.Layout;
        }

        cache.Layout?.Dispose();
        cache.Layout = CreateTextLayout(text, fontSize, weight, brush, alignment, maxWidth);
        cache.Text = text;
        cache.FontSize = fontSize;
        cache.Weight = weight;
        cache.Brush = brush;
        cache.Alignment = alignment;
        cache.MaxWidth = maxWidth;
        return cache.Layout;
    }

    private static TextLayout CreateStaticLayout(string text, double fontSize, FontWeight weight, IBrush brush, TextAlignment alignment)
        => new(text, ResolveTypeface(weight), fontSize, brush, alignment, TextWrapping.NoWrap, textTrimming: null, textDecorations: null, flowDirection: FlowDirection.LeftToRight, maxWidth: double.PositiveInfinity);

    private static TextLayout CreateTextLayout(string text, double fontSize, FontWeight weight, IBrush brush, TextAlignment alignment, double maxWidth)
    {
        var typeface = ResolveTypeface(weight);
        var constraint = double.IsFinite(maxWidth) && maxWidth > 0 ? maxWidth : double.PositiveInfinity;
        return new TextLayout(text, typeface, fontSize, brush, alignment, TextWrapping.NoWrap, textTrimming: null, textDecorations: null, flowDirection: FlowDirection.LeftToRight, maxWidth: constraint);
    }

    private static Typeface ResolveTypeface(FontWeight weight)
    {
        if (weight == FontWeight.Normal)
        {
            return LabelTypeface;
        }

        if (weight == FontWeight.SemiBold)
        {
            return LabelTypefaceBold;
        }

        return new Typeface("Inter", FontStyle.Normal, weight);
    }

    private sealed class ActivityLineRenderBuilder : IDisposable
    {
        private readonly Rect _rect;
        private readonly DateTimeOffset _minTimestamp;
        private readonly double _totalSeconds;
        private readonly double _minDuration;
        private readonly double _range;

        private Point[]? _points;
        private SKPoint[]? _skPoints;
        private int _count;
        private bool _skTransferred;
        private Point _lastPoint;
        private bool _hasLastPoint;

        private ActivityLineRenderBuilder(Rect rect, DateTimeOffset minTimestamp, double totalSeconds, double minDuration, double maxDuration)
        {
            _rect = rect;
            _minTimestamp = minTimestamp;
            _totalSeconds = totalSeconds;
            _minDuration = minDuration;
            _range = Math.Max(0.0001, maxDuration - minDuration);
        }

        public static ActivityLineRenderBuilder? Create(Rect rect, IReadOnlyList<ActivityPoint> points, DateTimeOffset minTimestamp, double totalSeconds, double minDuration, double maxDuration)
        {
            var builder = new ActivityLineRenderBuilder(rect, minTimestamp, totalSeconds, minDuration, maxDuration);
            return builder.Build(points) ? builder : null;
        }

        public int PointCount => _count;
        public bool HasLastPoint => _hasLastPoint;
        public Point LastPoint => _lastPoint;

        private bool Build(IReadOnlyList<ActivityPoint> points)
        {
            var count = points.Count;
            if (count == 0)
            {
                return false;
            }

            var width = Math.Max(1, _rect.Width);
            var height = Math.Max(1, _rect.Height);
            var pointArray = ArrayPool<Point>.Shared.Rent(count);
            var skArray = ArrayPool<SKPoint>.Shared.Rent(count);

            for (var i = 0; i < count; i++)
            {
                var point = points[i];

                double progress;
                if (_totalSeconds <= 0)
                {
                    progress = count <= 1 ? 0 : (double)i / (count - 1);
                }
                else
                {
                    var elapsed = (point.Timestamp - _minTimestamp).TotalSeconds;
                    progress = Math.Clamp(elapsed / _totalSeconds, 0, 1);
                }

                var normalized = Math.Clamp((point.DurationMilliseconds - _minDuration) / _range, 0, 1);
                var x = _rect.Left + progress * width;
                var y = _rect.Bottom - normalized * height;
                var mapped = new Point(x, y);
                pointArray[i] = mapped;
                skArray[i] = new SKPoint((float)x, (float)y);
            }

            _points = pointArray;
            _skPoints = skArray;
            _count = count;
            _hasLastPoint = count > 0;
            _lastPoint = count > 0 ? pointArray[count - 1] : default;
            return true;
        }

        public ActivityLineDrawOperation? CreateSkiaOperation(Color color)
        {
            if (_skPoints is null || _count == 0)
            {
                return null;
            }

            _skTransferred = true;
            var operation = new ActivityLineDrawOperation(_rect, _skPoints, _count, color);
            _skPoints = null;
            return operation;
        }

        public void DrawFallback(DrawingContext context, IBrush brush)
        {
            if (_points is null || _count == 0)
            {
                return;
            }

            var pen = new Pen(brush, 2) { LineJoin = PenLineJoin.Round };
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(_points[0], false);
                for (var i = 1; i < _count; i++)
                {
                    ctx.LineTo(_points[i]);
                }
            }

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

    private sealed class ActivityLineDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly SKPoint[] _points;
        private readonly int _count;
        private readonly Color _color;

        public ActivityLineDrawOperation(Rect bounds, SKPoint[] points, int count, Color color)
        {
            _bounds = bounds;
            _points = points;
            _count = count;
            _color = color;
        }

        public Rect Bounds => _bounds;

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
            if (feature is null)
            {
                return;
            }

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;

            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(_color.R, _color.G, _color.B, _color.A),
                StrokeWidth = 2f,
                StrokeJoin = SKStrokeJoin.Round,
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = true
            };

            using var path = new SKPath();
            path.MoveTo(_points[0]);
            for (var i = 1; i < _count; i++)
            {
                path.LineTo(_points[i]);
            }

            canvas.DrawPath(path, paint);
        }

        public void Dispose()
        {
            ArrayPool<SKPoint>.Shared.Return(_points);
        }

        public bool HitTest(Point p) => _bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
    }

    private sealed class CachedLayout
    {
        public string? Text;
        public double FontSize;
        public FontWeight Weight;
        public IBrush? Brush;
        public TextAlignment Alignment;
        public double MaxWidth;
        public TextLayout? Layout;
    }

    private readonly struct ActivityMetrics
    {
        public ActivityMetrics(DateTimeOffset minTimestamp, DateTimeOffset maxTimestamp, double minDuration, double maxDuration, double averageDuration, double totalSeconds)
        {
            MinTimestamp = minTimestamp;
            MaxTimestamp = maxTimestamp;
            MinDuration = minDuration;
            MaxDuration = maxDuration;
            AverageDuration = averageDuration;
            TotalSeconds = totalSeconds;
        }

        public DateTimeOffset MinTimestamp { get; }
        public DateTimeOffset MaxTimestamp { get; }
        public double MinDuration { get; }
        public double MaxDuration { get; }
        public double AverageDuration { get; }
        public double TotalSeconds { get; }
    }
}
