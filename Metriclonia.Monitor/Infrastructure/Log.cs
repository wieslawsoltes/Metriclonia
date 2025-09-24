using System;
using Microsoft.Extensions.Logging;

namespace Metriclonia.Monitor.Infrastructure;

internal static class Log
{
    private static readonly ILoggerFactory s_factory = LoggerFactory.Create(builder =>
    {
        builder
            .ClearProviders()
            .AddSimpleConsole(options =>
            {
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                options.IncludeScopes = true;
            })
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter("Microsoft", LogLevel.Warning)
            .AddFilter("System", LogLevel.Warning)
            .AddFilter("Avalonia", LogLevel.Information);
    });

    public static ILoggerFactory Factory => s_factory;

    public static ILogger<T> For<T>() => s_factory.CreateLogger<T>();

    public static ILogger App { get; } = s_factory.CreateLogger("Metriclonia.Monitor.App");

    public static void Shutdown()
    {
        s_factory.Dispose();
    }
}
