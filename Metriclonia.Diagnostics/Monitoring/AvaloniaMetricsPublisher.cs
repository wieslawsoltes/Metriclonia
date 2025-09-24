using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Metriclonia.Contracts.Monitoring;
using Metriclonia.Contracts.Serialization;

namespace Metriclonia.Diagnostics.Monitoring;

internal sealed class AvaloniaMetricsPublisher : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ActivityListener _activityListener;
    private const int ChannelCapacity = 32 * 1024;

    private readonly Channel<MonitoringEnvelope> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _senderTask;
    private readonly Timer _observableTimer;
    private readonly UdpClient _udpClient;
    private readonly TimeSpan _observableInterval;
    private readonly EnvelopeEncoding _encoding;

    public AvaloniaMetricsPublisher(MetricloniaMonitoringOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _udpClient = new UdpClient();
        _udpClient.Connect(options.Host, options.Port);

        _observableInterval = options.ObservableInterval;
        _encoding = options.Encoding;
        _channel = Channel.CreateBounded<MonitoringEnvelope>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite
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

        _senderTask = Task.Factory.StartNew(
            SendAsync,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
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
        var value = ConvertMeasurementToDouble(in measurement);
        Dictionary<string, string?>? tagDictionary = null;

        if (!tags.IsEmpty)
        {
            tagDictionary = new Dictionary<string, string?>(tags.Length, StringComparer.Ordinal);
            PopulateTags(tagDictionary, tags);
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

    private static void PopulateTags(Dictionary<string, string?> target, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        ref var start = ref MemoryMarshal.GetReference(tags);
        for (var i = 0; i < tags.Length; i++)
        {
            ref readonly var tag = ref Unsafe.Add(ref start, i);
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(target, tag.Key, out _);
            slot = tag.Value switch
            {
                null => null,
                string s => s,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => tag.Value.ToString()
            };
        }
    }

    private static double ConvertMeasurementToDouble<T>(in T measurement)
    {
        if (typeof(T) == typeof(double))
        {
            return Unsafe.As<T, double>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(float))
        {
            return Unsafe.As<T, float>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(decimal))
        {
            var dec = Unsafe.As<T, decimal>(ref Unsafe.AsRef(in measurement));
            return (double)dec;
        }

        if (typeof(T) == typeof(long))
        {
            return Unsafe.As<T, long>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(ulong))
        {
            return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(int))
        {
            return Unsafe.As<T, int>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(uint))
        {
            return Unsafe.As<T, uint>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(short))
        {
            return Unsafe.As<T, short>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(ushort))
        {
            return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(byte))
        {
            return Unsafe.As<T, byte>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(sbyte))
        {
            return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in measurement));
        }

        return Convert.ToDouble(measurement, CultureInfo.InvariantCulture);
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
                        var payload = MonitoringEnvelopeSerializer.Serialize(sample, _encoding);
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

    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllData;

    private static ActivitySamplingResult SampleAllDataUsingParentId(ref ActivityCreationOptions<string> _)
        => ActivitySamplingResult.AllData;
}
