using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Metriclonia.Monitor.Metrics;

internal sealed class ActivitySample
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("startTimestamp")]
    public DateTimeOffset StartTimestamp { get; set; }

    [JsonPropertyName("durationMilliseconds")]
    public double DurationMilliseconds { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("statusDescription")]
    public string? StatusDescription { get; set; }

    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = string.Empty;

    [JsonPropertyName("spanId")]
    public string SpanId { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public Dictionary<string, string?>? Tags { get; set; }
}
