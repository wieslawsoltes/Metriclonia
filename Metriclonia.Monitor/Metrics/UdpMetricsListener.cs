using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Metriclonia.Monitor.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Metriclonia.Monitor.Metrics;

internal sealed class UdpMetricsListener : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly ILogger Logger = Log.For<UdpMetricsListener>();

    private readonly int _port;
    private readonly UdpClient _udpClient;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public UdpMetricsListener(int port)
    {
        _port = port;
        _udpClient = new UdpClient(AddressFamily.InterNetwork);
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        Logger.LogInformation("UDP listener bound to port {Port}", port);
    }

    public event Action<MetricSample>? MetricReceived;

    public event Action<ActivitySample>? ActivityReceived;

    public void Start()
    {
        if (_listenTask is null)
        {
            Logger.LogInformation("Starting UDP receive loop on {Port}", _port);
            _listenTask = Task.Run(ListenAsync);
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udpClient.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogDebug("UDP receive canceled");
                    break;
                }

                try
                {
                    var envelope = JsonSerializer.Deserialize<MonitoringEnvelope>(result.Buffer, s_jsonOptions);
                    if (envelope is null)
                    {
                        Logger.LogWarning("Received payload could not be deserialized ({Length} bytes)", result.Buffer.Length);
                        continue;
                    }

                    if (string.Equals(envelope.Type, "metric", StringComparison.OrdinalIgnoreCase) && envelope.Metric is not null)
                    {
                        var sample = envelope.Metric;
                        Logger.LogTrace("Received metric payload for {Meter}/{Instrument}", sample.MeterName, sample.InstrumentName);
                        MetricReceived?.Invoke(sample);
                    }
                    else if (string.Equals(envelope.Type, "activity", StringComparison.OrdinalIgnoreCase) && envelope.Activity is not null)
                    {
                        var activity = envelope.Activity;
                        Logger.LogTrace("Received activity payload for {Name}", activity.Name);
                        ActivityReceived?.Invoke(activity);
                    }
                    else if (envelope.Metric is not null && string.IsNullOrEmpty(envelope.Type))
                    {
                        // Back-compat: metrics prior to envelope introduction.
                        var sample = envelope.Metric;
                        Logger.LogTrace("Received legacy metric payload for {Meter}/{Instrument}", sample.MeterName, sample.InstrumentName);
                        MetricReceived?.Invoke(sample);
                    }
                    else
                    {
                        try
                        {
                            var legacyMetric = JsonSerializer.Deserialize<MetricSample>(result.Buffer, s_jsonOptions);
                            if (legacyMetric is not null && legacyMetric.Timestamp != default)
                            {
                                Logger.LogTrace("Received legacy metric payload for {Meter}/{Instrument}", legacyMetric.MeterName, legacyMetric.InstrumentName);
                                MetricReceived?.Invoke(legacyMetric);
                                continue;
                            }
                        }
                        catch (JsonException legacyEx)
                        {
                            Logger.LogDebug(legacyEx, "Legacy metric payload deserialization failed");
                        }

                        Logger.LogWarning("Received payload with unknown type '{Type}'", envelope.Type);
                    }
                }
                catch (JsonException ex)
                {
                    Logger.LogWarning(ex, "Failed to deserialize metric payload ({Length} bytes)", result.Buffer.Length);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Unhandled exception while parsing metric payload");
                }
            }
        }
        finally
        {
            _udpClient.Close();
            Logger.LogInformation("UDP listener on port {Port} stopped", _port);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Ignoring exception while awaiting UDP listener shutdown");
            }
        }

        _udpClient.Dispose();
        _cts.Dispose();
        Logger.LogInformation("UDP listener disposed");
    }
}
