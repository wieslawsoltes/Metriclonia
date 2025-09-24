using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Metriclonia.Diagnostics.Monitoring;

internal sealed class AvaloniaMetricsPublisher : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly MeterListener _listener;
    private readonly ActivityListener _activityListener;
    private readonly Channel<MonitoringEnvelope> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _senderTask;
    private readonly Timer _observableTimer;
    private readonly UdpClient _udpClient;
    private readonly TimeSpan _observableInterval;

    public AvaloniaMetricsPublisher(string host, int port, TimeSpan? observableInterval = null)
    {
        _udpClient = new UdpClient();
        _udpClient.Connect(host, port);

        _observableInterval = observableInterval ?? TimeSpan.FromMilliseconds(500);
        _channel = Channel.CreateUnbounded<MonitoringEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name.StartsWith("Avalonia", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<decimal>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.SetMeasurementEventCallback<float>(OnMeasurement);
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<int>(OnMeasurement);
        _listener.SetMeasurementEventCallback<short>(OnMeasurement);
        _listener.SetMeasurementEventCallback<byte>(OnMeasurement);

        _listener.Start();

        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DiagnosticSourceName,
            Sample = SampleAllData,
            SampleUsingParentId = SampleAllDataUsingParentId,
            ActivityStopped = OnActivityStopped
        };

        ActivitySource.AddActivityListener(_activityListener);

        _observableTimer = new Timer(_ =>
        {
            try
            {
                _listener.RecordObservableInstruments();
            }
            catch
            {
                // Swallow to avoid terminating timer loop; listener keeps working.
            }
        }, null, _observableInterval, _observableInterval);

        _senderTask = Task.Run(SendAsync);
    }

    private void OnMeasurement<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (!_channel.Writer.TryWrite(MonitoringEnvelope.FromMetric(CreateSample(instrument, measurement, tags))))
        {
            // Channel is full or completed; drop the measurement to avoid blocking instrument threads.
        }
    }

    private static MetricSample CreateSample<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        double value = measurement switch
        {
            double d => d,
            float f => f,
            decimal dec => (double)dec,
            long l => l,
            ulong ul => ul,
            int i => i,
            uint ui => ui,
            short s => s,
            ushort us => us,
            byte b => b,
            sbyte sb => sb,
            _ => Convert.ToDouble(measurement)
        };

        var tagDictionary = tags.Length == 0
            ? null
            : new Dictionary<string, string?>(tags.Length, StringComparer.Ordinal);

        if (tagDictionary is not null)
        {
            foreach (var tag in tags)
            {
                tagDictionary[tag.Key] = tag.Value?.ToString();
            }
        }

        return new MetricSample
        {
            Timestamp = DateTimeOffset.UtcNow,
            MeterName = instrument.Meter.Name,
            InstrumentName = instrument.Name,
            InstrumentType = instrument.GetType().Name,
            Unit = instrument.Unit,
            Description = instrument.Description,
            Value = value,
            ValueType = typeof(T).Name,
            Tags = tagDictionary
        };
    }

    private async Task SendAsync()
    {
        try
        {
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var sample))
                {
                    try
                    {
                        var payload = JsonSerializer.SerializeToUtf8Bytes(sample, s_jsonOptions);
                        await _udpClient.SendAsync(payload, payload.Length).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        // Ignore individual send failures, continue streaming metrics.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Dispose();
        _activityListener.Dispose();
        _observableTimer.Dispose();
        _channel.Writer.TryComplete();
        try
        {
            _senderTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Suppress dispose-time exceptions.
        }
        _udpClient.Dispose();
        _cts.Dispose();
    }

    private void OnActivityStopped(Activity activity)
    {
        if (activity.Source.Name != DiagnosticSourceName)
        {
            return;
        }

        var tags = activity.Tags;
        Dictionary<string, string?>? tagDictionary = null;

        if (tags is not null)
        {
            foreach (var tag in tags)
            {
                tagDictionary ??= new Dictionary<string, string?>(StringComparer.Ordinal);
                tagDictionary[tag.Key] = tag.Value;
            }
        }

        var sample = new ActivitySample
        {
            Name = activity.DisplayName,
            StartTimestamp = activity.StartTimeUtc,
            DurationMilliseconds = activity.Duration.TotalMilliseconds,
            Status = activity.Status.ToString(),
            StatusDescription = activity.StatusDescription,
            TraceId = activity.TraceId.ToString(),
            SpanId = activity.SpanId.ToString(),
            Tags = tagDictionary
        };

        if (!_channel.Writer.TryWrite(MonitoringEnvelope.FromActivity(sample)))
        {
            // Channel is full or completed; drop the activity to avoid blocking instrumentation.
        }
    }

    private const string DiagnosticSourceName = "Avalonia.Diagnostic.Source";

    private sealed class MetricSample
    {
        public DateTimeOffset Timestamp { get; init; }

        public string MeterName { get; init; } = string.Empty;

        public string InstrumentName { get; init; } = string.Empty;

        public string InstrumentType { get; init; } = string.Empty;

        public string? Unit { get; init; }

        public string? Description { get; init; }

        public double Value { get; init; }

        public string ValueType { get; init; } = string.Empty;

        public Dictionary<string, string?>? Tags { get; init; }
    }

    private sealed class ActivitySample
    {
        public string Name { get; init; } = string.Empty;

        public DateTimeOffset StartTimestamp { get; init; }

        public double DurationMilliseconds { get; init; }

        public string Status { get; init; } = string.Empty;

        public string? StatusDescription { get; init; }

        public string TraceId { get; init; } = string.Empty;

        public string SpanId { get; init; } = string.Empty;

        public Dictionary<string, string?>? Tags { get; init; }
    }

    private sealed class MonitoringEnvelope
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("metric")]
        public MetricSample? Metric { get; init; }

        [JsonPropertyName("activity")]
        public ActivitySample? Activity { get; init; }

        public static MonitoringEnvelope FromMetric(MetricSample sample)
            => new()
            {
                Type = "metric",
                Metric = sample
            };

        public static MonitoringEnvelope FromActivity(ActivitySample sample)
            => new()
            {
                Type = "activity",
                Activity = sample
            };
    }

    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllData;

    private static ActivitySamplingResult SampleAllDataUsingParentId(ref ActivityCreationOptions<string> _)
        => ActivitySamplingResult.AllData;
}
