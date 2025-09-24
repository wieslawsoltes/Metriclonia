using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Metriclonia.Monitor.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Metriclonia.Monitor;

public partial class App : Application
{
    private static readonly ILogger Logger = Log.App;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Logger.LogInformation("Application resources initialized");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += OnExit;
            Logger.LogInformation("Desktop lifetime started");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        Log.App.LogInformation("Application shutting down with exit code {ExitCode}", e.ApplicationExitCode);
        Log.Shutdown();
    }
}
