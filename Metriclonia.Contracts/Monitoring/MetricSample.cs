using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Metriclonia.Contracts.Monitoring;

public sealed class MetricSample
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("meterName")]
    public string MeterName { get; set; } = string.Empty;

    [JsonPropertyName("instrumentName")]
    public string InstrumentName { get; set; } = string.Empty;

    [JsonPropertyName("instrumentType")]
    public string InstrumentType { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("valueType")]
    public string ValueType { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public Dictionary<string, string?>? Tags { get; set; }
}
