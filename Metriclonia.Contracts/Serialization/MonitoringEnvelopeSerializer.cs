using System;
using Metriclonia.Contracts.Monitoring;

namespace Metriclonia.Contracts.Serialization;

public static class MonitoringEnvelopeSerializer
{
    public static byte[] Serialize(MonitoringEnvelope envelope, EnvelopeEncoding encoding)
        => encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.Serialize(envelope),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.Serialize(envelope),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };

    public static bool TryDeserialize(ReadOnlyMemory<byte> payload, EnvelopeEncoding encoding, out MonitoringEnvelope? envelope)
    {
        return encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.TryDeserialize(payload.Span, out envelope),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.TryDeserialize(payload, out envelope),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };
    }

    public static bool TryDeserialize(ReadOnlyMemory<byte> payload, out MonitoringEnvelope? envelope)
    {
        if (JsonEnvelopeSerializer.TryDeserialize(payload.Span, out envelope))
        {
            return true;
        }

        return BinaryEnvelopeSerializer.TryDeserialize(payload, out envelope);
    }
}
