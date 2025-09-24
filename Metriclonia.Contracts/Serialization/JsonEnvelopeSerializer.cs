using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Metriclonia.Contracts.Monitoring;

namespace Metriclonia.Contracts.Serialization;

public static class JsonEnvelopeSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static byte[] Serialize(MonitoringEnvelope envelope)
        => JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, out MonitoringEnvelope? envelope)
    {
        try
        {
            envelope = JsonSerializer.Deserialize<MonitoringEnvelope>(payload, Options);
            return envelope is not null;
        }
        catch (JsonException)
        {
            envelope = null;
            return false;
        }
    }
}
