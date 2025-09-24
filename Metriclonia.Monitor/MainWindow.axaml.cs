using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
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
        _viewModel = new MetricsDashboardViewModel(port);
        DataContext = _viewModel;

        Closed += OnClosed;
        Logger.LogInformation("Main window initialized. Bound port {Port}", port);
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
