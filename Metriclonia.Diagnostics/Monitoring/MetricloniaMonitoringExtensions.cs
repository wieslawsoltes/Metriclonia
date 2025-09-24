using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace Metriclonia.Diagnostics.Monitoring;

public static class MetricloniaMonitoringExtensions
{
    public static IDisposable AttachMetricloniaMonitoring(this Application application)
    {
        return AttachMetricloniaMonitoring(application, new MetricloniaMonitoringOptions());
    }

    public static IDisposable AttachMetricloniaMonitoring(this Application application, MetricloniaMonitoringOptions options)
    {
        if (application is null)
        {
            throw new ArgumentNullException(nameof(application));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var publisher = new AvaloniaMetricsPublisher(options.Host, options.Port, options.ObservableInterval);
        var handle = new MetricloniaMonitoringHandle(publisher);

        if (application.ApplicationLifetime is IControlledApplicationLifetime lifetime)
        {
            handle.RegisterLifetime(lifetime);
        }

        return handle;
    }

    private sealed class MetricloniaMonitoringHandle : IDisposable
    {
        private readonly AvaloniaMetricsPublisher _publisher;
        private IControlledApplicationLifetime? _lifetime;
        private EventHandler<ControlledApplicationLifetimeExitEventArgs>? _exitHandler;
        private int _disposed;

        public MetricloniaMonitoringHandle(AvaloniaMetricsPublisher publisher)
        {
            _publisher = publisher;
        }

        public void RegisterLifetime(IControlledApplicationLifetime lifetime)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _exitHandler = (_, _) => Dispose();
            _lifetime = lifetime;
            lifetime.Exit += _exitHandler;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_lifetime is not null && _exitHandler is not null)
            {
                _lifetime.Exit -= _exitHandler;
            }

            _publisher.Dispose();
        }
    }
}
