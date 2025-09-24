using System.Text.Json.Serialization;

namespace Metriclonia.Contracts.Monitoring;

public sealed class MonitoringEnvelope
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("metric")]
    public MetricSample? Metric { get; set; }

    [JsonPropertyName("activity")]
    public ActivitySample? Activity { get; set; }

    public static MonitoringEnvelope FromMetric(MetricSample sample)
        => new()
        {
            Type = EnvelopeTypes.Metric,
            Metric = sample
        };

    public static MonitoringEnvelope FromActivity(ActivitySample sample)
        => new()
        {
            Type = EnvelopeTypes.Activity,
            Activity = sample
        };
}

public static class EnvelopeTypes
{
    public const string Metric = "metric";
    public const string Activity = "activity";
}
