using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Metriclonia.Producer.Controls;

public class ParticleFieldControl : Control
{
    private const int DefaultParticleCount = 180;

    private readonly List<Particle> _particles = new();
    private readonly Random _random = new();
    private readonly DispatcherTimer _timer;

    private bool _isInitialized;
    private bool _isAttached;

    public static readonly StyledProperty<bool> IsPausedProperty =
        AvaloniaProperty.Register<ParticleFieldControl, bool>(nameof(IsPaused));

    public ParticleFieldControl()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Advance());
        this.AttachedToVisualTree += OnAttachedToVisualTree;
        this.DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public bool IsPaused
    {
        get => GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (!_isInitialized && bounds.Width > 0 && bounds.Height > 0)
        {
            InitializeParticles(bounds);
            _isInitialized = true;
        }

        if (_particles.Count == 0)
        {
            return;
        }

        var background = new LinearGradientBrush
        {
            GradientStops = new GradientStops
            {
                new GradientStop(Color.FromArgb(255, 12, 16, 32), 0),
                new GradientStop(Color.FromArgb(255, 32, 48, 82), 1)
            }
        };
        context.FillRectangle(background, bounds);

        var connectionBrush = new SolidColorBrush(Color.FromArgb(80, 173, 216, 230));
        var connectionPen = new Pen(connectionBrush, 0.8);

        for (var i = 0; i < _particles.Count; i++)
        {
            var particle = _particles[i];
            var center = particle.Position;
            var radius = particle.Radius;
            var brush = particle.Brush;
            context.DrawEllipse(brush, null, center, radius, radius);

            for (var j = i + 1; j < _particles.Count; j++)
            {
                var other = _particles[j];
                var dx = center.X - other.Position.X;
                var dy = center.Y - other.Position.Y;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance < 120)
                {
                    var alpha = (byte)Math.Clamp(120 - distance, 15, 120);
                    connectionBrush.Color = Color.FromArgb(alpha, 173, 216, 230);
                    context.DrawLine(connectionPen, center, other.Position);
                }
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsPausedProperty)
        {
            UpdateTimerState();
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;
        UpdateTimerState();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        UpdateTimerState();
    }

    private void InitializeParticles(Rect bounds)
    {
        _particles.Clear();
        for (var i = 0; i < DefaultParticleCount; i++)
        {
            _particles.Add(CreateParticle(bounds));
        }
    }

    private void Advance()
    {
        if (!_isInitialized)
        {
            return;
        }

        var bounds = Bounds;
        for (var index = 0; index < _particles.Count; index++)
        {
            var particle = _particles[index];
            var position = particle.Position + particle.Velocity;

            if (position.X < 0 || position.X > bounds.Width)
            {
                particle.Velocity = new Vector(-particle.Velocity.X, particle.Velocity.Y);
                position = new Point(Math.Clamp(position.X, 0, bounds.Width), position.Y);
            }

            if (position.Y < 0 || position.Y > bounds.Height)
            {
                particle.Velocity = new Vector(particle.Velocity.X, -particle.Velocity.Y);
                position = new Point(position.X, Math.Clamp(position.Y, 0, bounds.Height));
            }

            particle.Position = position;
            particle.Radius = Math.Clamp(particle.Radius + (float)(_random.NextDouble() - 0.5) * 0.3f, 2.5f, 6.5f);
            _particles[index] = particle;
        }

        InvalidateVisual();
    }

    private void UpdateTimerState()
    {
        if (!_isAttached || IsPaused)
        {
            if (_timer.IsEnabled)
            {
                _timer.Stop();
            }
            return;
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private Particle CreateParticle(Rect bounds)
    {
        var angle = _random.NextDouble() * Math.PI * 2;
        var speed = 0.75 + _random.NextDouble() * 1.35;
        return new Particle
        {
            Position = new Point(_random.NextDouble() * bounds.Width, _random.NextDouble() * bounds.Height),
            Velocity = new Vector(Math.Cos(angle) * speed, Math.Sin(angle) * speed),
            Radius = 3 + _random.NextDouble() * 3,
            Brush = new SolidColorBrush(Color.FromArgb(200, (byte)_random.Next(120, 240), (byte)_random.Next(150, 255), (byte)_random.Next(180, 255)))
        };
    }

    private struct Particle
    {
        public Point Position { get; set; }

        public Vector Velocity { get; set; }

        public double Radius { get; set; }

        public SolidColorBrush Brush { get; set; }
    }
}
