using System.Text.Json.Serialization;

namespace Metriclonia.Monitor.Metrics;

internal sealed class MonitoringEnvelope
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("metric")]
    public MetricSample? Metric { get; set; }

    [JsonPropertyName("activity")]
    public ActivitySample? Activity { get; set; }
}
