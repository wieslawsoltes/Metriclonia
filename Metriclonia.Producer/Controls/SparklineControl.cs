using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Metriclonia.Producer.Controls;

public class SparklineControl : Control
{
    public static readonly StyledProperty<IList<double>?> ValuesProperty =
        AvaloniaProperty.Register<SparklineControl, IList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush> StrokeProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush>(nameof(Stroke), Brushes.DeepSkyBlue);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<SparklineControl, double>(nameof(StrokeThickness), 1.5);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<SparklineControl, IBrush?>(nameof(Fill), new SolidColorBrush(Color.FromArgb(64, 135, 206, 250)));

    static SparklineControl()
    {
        AffectsRender<SparklineControl>(ValuesProperty, StrokeProperty, StrokeThicknessProperty, FillProperty);
    }

    public IList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var values = Values;
        if (values is null || values.Count < 2)
        {
            return;
        }

        var bounds = Bounds.WithX(0).WithY(0);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (var value in values)
        {
            if (value < min)
            {
                min = value;
            }
            if (value > max)
            {
                max = value;
            }
        }

        if (Math.Abs(max - min) < 0.001)
        {
            max = min + 1;
        }

        var scaleX = bounds.Width / Math.Max(1, values.Count - 1);
        var scaleY = bounds.Height / (max - min);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var firstPoint = MapPoint(0, values[0]);
            ctx.BeginFigure(firstPoint, Fill is not null);
            for (var i = 1; i < values.Count; i++)
            {
                ctx.LineTo(MapPoint(i, values[i]));
            }

            if (Fill is not null)
            {
                ctx.LineTo(new Point(bounds.Width, bounds.Height));
                ctx.LineTo(new Point(0, bounds.Height));
                ctx.EndFigure(true);
            }
            else
            {
                ctx.EndFigure(false);
            }
        }

        if (Fill is not null)
        {
            context.DrawGeometry(Fill, null, geometry);
        }

        var pen = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round };
        context.DrawGeometry(null, pen, geometry);

        Point MapPoint(int index, double value)
        {
            var x = index * scaleX;
            var normalized = (value - min) * scaleY;
            var y = bounds.Height - normalized;
            return new Point(x, y);
        }
    }
}
