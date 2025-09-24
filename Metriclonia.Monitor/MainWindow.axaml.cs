using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Metriclonia.Contracts.Serialization;
using Metriclonia.Monitor.Infrastructure;
using Metriclonia.Monitor.ViewModels;
using Microsoft.Extensions.Logging;

namespace Metriclonia.Monitor;

public partial class MainWindow : Window
{
    private MetricsDashboardViewModel? _viewModel;
    private static readonly ILogger Logger = Log.For<MainWindow>();

    public MainWindow()
    {
        InitializeComponent();

        var port = ResolvePort();
        var encoding = ResolveEncoding();
        _viewModel = new MetricsDashboardViewModel(port, encoding);
        DataContext = _viewModel;

        Closed += OnClosed;
        Logger.LogInformation("Main window initialized. Bound port {Port} (preferred encoding {Encoding})", port, encoding);
    }

    private static int ResolvePort()
    {
        var env = Environment.GetEnvironmentVariable("METRICLONIA_METRICS_PORT");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 && parsed < 65535)
        {
            return parsed;
        }

        return 5005;
    }

    private static EnvelopeEncoding ResolveEncoding()
    {
        var env = Environment.GetEnvironmentVariable("METRICLONIA_PAYLOAD_ENCODING");
        if (string.IsNullOrWhiteSpace(env))
        {
            return EnvelopeEncoding.Json;
        }

        return env.Trim().ToLowerInvariant() switch
        {
            "binary" => EnvelopeEncoding.Binary,
            "cbor" => EnvelopeEncoding.Binary,
            "json" => EnvelopeEncoding.Json,
            "text" => EnvelopeEncoding.Json,
            _ => EnvelopeEncoding.Json
        };
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        Logger.LogInformation("Main window closing. Disposing view model");
        await _viewModel.DisposeAsync();
        _viewModel = null;
    }
}
