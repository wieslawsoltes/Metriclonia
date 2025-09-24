using System;
using System.Collections.Generic;
using System.Formats.Cbor;

namespace Metriclonia.Monitor.Metrics;

internal static class BinaryEnvelopeSerializer
{
    public static bool TryDeserialize(byte[] payload, out MonitoringEnvelope? envelope)
    {
        try
        {
            var reader = new CborReader(payload.AsMemory(), CborConformanceMode.Lax);
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
