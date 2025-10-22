using System;
using System.Collections.Generic;
using System.Formats.Cbor;
using Metriclonia.Contracts.Monitoring;

namespace Metriclonia.Contracts.Serialization;

public static class BinaryEnvelopeSerializer
{
    public static byte[] Serialize(MonitoringEnvelope envelope)
    {
        var writer = new CborWriter();
        WriteEnvelope(writer, envelope);
        return writer.Encode();
    }

    public static bool TryDeserialize(ReadOnlyMemory<byte> payload, out MonitoringEnvelope? envelope)
    {
        try
        {
            var reader = new CborReader(payload, CborConformanceMode.Lax);
            envelope = ReadEnvelope(reader);
            return envelope is not null && reader.BytesRemaining == 0;
        }
        catch (CborContentException)
        {
            envelope = null;
            return false;
        }
        catch (ArgumentException)
        {
            envelope = null;
            return false;
        }
    }

    private static void WriteEnvelope(CborWriter writer, MonitoringEnvelope envelope)
    {
        writer.WriteStartMap(null);

        if (!string.IsNullOrEmpty(envelope.Type))
        {
            writer.WriteTextString("type");
            writer.WriteTextString(envelope.Type);
        }

        if (envelope.Metric is not null)
        {
            writer.WriteTextString("metric");
            WriteMetric(writer, envelope.Metric);
        }

        if (envelope.Activity is not null)
        {
            writer.WriteTextString("activity");
            WriteActivity(writer, envelope.Activity);
        }

        writer.WriteEndMap();
    }

    private static void WriteMetric(CborWriter writer, MetricSample sample)
    {
        writer.WriteStartMap(null);

        writer.WriteTextString("timestamp");
        writer.WriteInt64(sample.Timestamp.ToUnixTimeMilliseconds());

        writer.WriteTextString("meterName");
        writer.WriteTextString(sample.MeterName);

        writer.WriteTextString("instrumentName");
        writer.WriteTextString(sample.InstrumentName);

        writer.WriteTextString("instrumentType");
        writer.WriteTextString(sample.InstrumentType);

        if (!string.IsNullOrEmpty(sample.Unit))
        {
            writer.WriteTextString("unit");
            writer.WriteTextString(sample.Unit);
        }

        if (!string.IsNullOrEmpty(sample.Description))
        {
            writer.WriteTextString("description");
            writer.WriteTextString(sample.Description);
        }

        writer.WriteTextString("value");
        writer.WriteDouble(sample.Value);

        writer.WriteTextString("valueType");
        writer.WriteTextString(sample.ValueType);

        if (sample.Tags is { Count: > 0 })
        {
            writer.WriteTextString("tags");
            WriteTags(writer, sample.Tags);
        }

        writer.WriteEndMap();
    }

    private static void WriteActivity(CborWriter writer, ActivitySample sample)
    {
        writer.WriteStartMap(null);

        writer.WriteTextString("name");
        writer.WriteTextString(sample.Name);

        writer.WriteTextString("startTimestamp");
        writer.WriteInt64(sample.StartTimestamp.ToUnixTimeMilliseconds());

        writer.WriteTextString("durationMilliseconds");
        writer.WriteDouble(sample.DurationMilliseconds);

        writer.WriteTextString("status");
        writer.WriteTextString(sample.Status);

        if (!string.IsNullOrEmpty(sample.StatusDescription))
        {
            writer.WriteTextString("statusDescription");
            writer.WriteTextString(sample.StatusDescription);
        }

        writer.WriteTextString("traceId");
        writer.WriteTextString(sample.TraceId);

        writer.WriteTextString("spanId");
        writer.WriteTextString(sample.SpanId);

        if (sample.Tags is { Count: > 0 })
        {
            writer.WriteTextString("tags");
            WriteTags(writer, sample.Tags);
        }

        writer.WriteEndMap();
    }

    private static void WriteTags(CborWriter writer, Dictionary<string, string?> tags)
    {
        writer.WriteStartMap(tags.Count);
        foreach (var (key, value) in tags)
        {
            writer.WriteTextString(key);
            if (value is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteTextString(value);
            }
        }

        writer.WriteEndMap();
    }

    private static MonitoringEnvelope? ReadEnvelope(CborReader reader)
    {
        if (reader.PeekState() != CborReaderState.StartMap)
        {
            return null;
        }

        var length = reader.ReadStartMap();
        string type = string.Empty;
        MetricSample? metric = null;
        ActivitySample? activity = null;

        for (var i = 0; length is null || i < length; i++)
        {
            if (reader.PeekState() == CborReaderState.EndMap)
            {
                reader.ReadEndMap();
                break;
            }

            var name = reader.ReadTextString();
            switch (name)
            {
                case "type":
                    type = reader.ReadTextString();
                    break;
                case "metric":
                    metric = ReadMetric(reader);
                    break;
                case "activity":
                    activity = ReadActivity(reader);
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        return new MonitoringEnvelope
        {
            Type = type,
            Metric = metric,
            Activity = activity
        };
    }

    private static MetricSample ReadMetric(CborReader reader)
    {
        var sample = new MetricSample();
        var length = reader.ReadStartMap();

        for (var i = 0; length is null || i < length; i++)
        {
            if (reader.PeekState() == CborReaderState.EndMap)
            {
                reader.ReadEndMap();
                break;
            }

            var name = reader.ReadTextString();
            switch (name)
            {
                case "timestamp":
                    sample.Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
                    break;
                case "meterName":
                    sample.MeterName = reader.ReadTextString();
                    break;
                case "instrumentName":
                    sample.InstrumentName = reader.ReadTextString();
                    break;
                case "instrumentType":
                    sample.InstrumentType = reader.ReadTextString();
                    break;
                case "unit":
                    sample.Unit = reader.ReadTextString();
                    break;
                case "description":
                    sample.Description = reader.ReadTextString();
                    break;
                case "value":
                    sample.Value = reader.ReadDouble();
                    break;
                case "valueType":
                    sample.ValueType = reader.ReadTextString();
                    break;
                case "tags":
                    sample.Tags = ReadTags(reader);
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        return sample;
    }

    private static ActivitySample ReadActivity(CborReader reader)
    {
        var sample = new ActivitySample();
        var length = reader.ReadStartMap();

        for (var i = 0; length is null || i < length; i++)
        {
            if (reader.PeekState() == CborReaderState.EndMap)
            {
                reader.ReadEndMap();
                break;
            }

            var name = reader.ReadTextString();
            switch (name)
            {
                case "name":
                    sample.Name = reader.ReadTextString();
                    break;
                case "startTimestamp":
                    sample.StartTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.ReadInt64());
                    break;
                case "durationMilliseconds":
                    sample.DurationMilliseconds = reader.ReadDouble();
                    break;
                case "status":
                    sample.Status = reader.ReadTextString();
                    break;
                case "statusDescription":
                    sample.StatusDescription = reader.ReadTextString();
                    break;
                case "traceId":
                    sample.TraceId = reader.ReadTextString();
                    break;
                case "spanId":
                    sample.SpanId = reader.ReadTextString();
                    break;
                case "tags":
                    sample.Tags = ReadTags(reader);
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        return sample;
    }

    private static Dictionary<string, string?>? ReadTags(CborReader reader)
    {
        if (reader.PeekState() == CborReaderState.Null)
        {
            reader.ReadNull();
            return null;
        }

        var length = reader.ReadStartMap();
        if (length == 0)
        {
            reader.ReadEndMap();
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        var tags = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; length is null || i < length; i++)
        {
            if (reader.PeekState() == CborReaderState.EndMap)
            {
                reader.ReadEndMap();
                break;
            }

            var key = reader.ReadTextString();

            string? value;
            if (reader.PeekState() == CborReaderState.Null)
            {
                reader.ReadNull();
                value = null;
            }
            else
            {
                value = reader.ReadTextString();
            }

            tags[key] = value;
        }

        return tags;
    }
}
