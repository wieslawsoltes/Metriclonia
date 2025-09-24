using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Metriclonia.Producer.Metrics;

namespace Metriclonia.Producer;

public partial class App : Application
{
    private AvaloniaMetricsPublisher? _metricsPublisher;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += OnDesktopExit;
        }

        var host = Environment.GetEnvironmentVariable("METRICLONIA_METRICS_HOST") ?? "127.0.0.1";
        var portValue = Environment.GetEnvironmentVariable("METRICLONIA_METRICS_PORT");
        var port = 5005;

        if (!string.IsNullOrWhiteSpace(portValue) && int.TryParse(portValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort))
        {
            port = parsedPort;
        }

        _metricsPublisher = new AvaloniaMetricsPublisher(host, port);

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _metricsPublisher?.Dispose();
        _metricsPublisher = null;
    }
}
