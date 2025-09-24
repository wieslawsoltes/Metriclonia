using System;

namespace Metriclonia.Diagnostics.Monitoring;

public sealed class MetricloniaMonitoringOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5005;

    public TimeSpan ObservableInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}
