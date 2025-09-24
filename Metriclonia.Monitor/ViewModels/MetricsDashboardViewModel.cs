using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Metriclonia.Contracts.Monitoring;
using Metriclonia.Contracts.Serialization;
using Metriclonia.Monitor.Infrastructure;
using Metriclonia.Monitor.Metrics;
using Metriclonia.Monitor.Visualization;
using Microsoft.Extensions.Logging;

namespace Metriclonia.Monitor.ViewModels;

public sealed class MetricsDashboardViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const double MinVisibleSeconds = 1;
    private const double MaxVisibleSeconds = 180;

    private readonly ObservableCollection<TimelineSeries> _series = new();
    private readonly Dictionary<string, TimelineSeries> _seriesLookup = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<MetricSample> _pending = new();
    private readonly ObservableCollection<ActivitySeries> _activities = new();
    private readonly Dictionary<string, ActivitySeries> _activitiesLookup = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ActivitySample> _pendingActivities = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly UdpMetricsListener _listener;
    private readonly TimeSpan _retention = TimeSpan.FromMinutes(20);
    private readonly ColorPalette _palette = new();
    private readonly ColorPalette _activityPalette = new();
    private readonly EventHandler _flushHandler;
    private readonly TriggerConfiguration _triggerConfiguration = new();
    private static readonly MetricSeed[] SeedMetrics =
    {
        new("Avalonia.Diagnostic.Meter", "avalonia.comp.render.time", "Histogram", "ms", "Duration of the compositor render pass on render thread", "Double"),
        new("Avalonia.Diagnostic.Meter", "avalonia.comp.update.time", "Histogram", "ms", "Duration of the compositor update pass on render thread", "Double"),
        new("Avalonia.Diagnostic.Meter", "avalonia.ui.measure.time", "Histogram", "ms", "Duration of layout measurement pass on UI thread", "Double"),
        new("Avalonia.Diagnostic.Meter", "avalonia.ui.arrange.time", "Histogram", "ms", "Duration of layout arrangement pass on UI thread", "Double"),
        new("Avalonia.Diagnostic.Meter", "avalonia.ui.render.time", "Histogram", "ms", "Duration of render recording pass on UI thread", "Double"),
        new("Avalonia.Diagnostic.Meter", "avalonia.ui.input.time", "Histogram", "ms", "Duration of input processing on UI thread", "Double"),
        new("Avalonia.Diagnostic.Meter", "avalonia.ui.event.handler.count", "ObservableUpDownCounter", "{handler}", "Number of event handlers currently registered in the application", "Int64"),
        new("Avalonia.Diagnostic.Meter", "avalonia.ui.visual.count", "ObservableUpDownCounter", "{visual}", "Number of visual elements currently present in the visual tree", "Int64"),
        new("Avalonia.Diagnostic.Meter", "avalonia.ui.dispatcher.timer.count", "ObservableUpDownCounter", "{timer}", "Number of active dispatcher timers in the application", "Int64")
    };
    private static readonly ActivitySeed[] SeedActivities =
    {
        new("Avalonia.AttachingStyle", "Style attachment phases when applying styles to controls"),
        new("Avalonia.FindingResource", "Resource resolution lookups across resource scopes"),
        new("Avalonia.EvaluatingStyle", "Evaluation of style activators and triggers"),
        new("Avalonia.MeasuringLayoutable", "Layoutable measure pass evaluations"),
        new("Avalonia.ArrangingLayoutable", "Layoutable arrange pass evaluations"),
        new("Avalonia.PerformingHitTest", "Hit testing operations on the visual tree"),
        new("Avalonia.RaisingRoutedEvent", "Routing of Avalonia routed events")
    };
    private static readonly ILogger Logger = Log.For<MetricsDashboardViewModel>();
    private const bool EnableMetricDetailLogging = false;

    private double _visibleDurationSeconds = 30;
    private double _ingressRate;
    private DateTimeOffset _lastIngressSample = DateTimeOffset.UtcNow;
    private int _ingressCounter;
    private bool _isRenderingPaused;

    public MetricsDashboardViewModel(int port, EnvelopeEncoding encoding = EnvelopeEncoding.Json)
    {
        ListeningPort = port;
        _listener = new UdpMetricsListener(port, encoding);
        _listener.MetricReceived += OnMetricReceived;
        _listener.ActivityReceived += OnActivityReceived;
        _listener.Start();
        Logger.LogInformation("Metrics dashboard listening on UDP port {Port}", port);

        _flushHandler = (_, _) => Flush();

        _flushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _flushTimer.Tick += _flushHandler;
        _flushTimer.Start();
        Logger.LogDebug("Flush timer started with interval {Interval}ms", _flushTimer.Interval.TotalMilliseconds);

        _series.CollectionChanged += OnSeriesCollectionChanged;

        SeedKnownMetricSeries();
        SeedKnownActivitySeries();
    }

    public ObservableCollection<TimelineSeries> Series => _series;

    public ObservableCollection<ActivitySeries> Activities => _activities;

    public int ListeningPort { get; }

    public TriggerConfiguration TriggerConfiguration => _triggerConfiguration;

    public double VisibleDurationSeconds
    {
        get => _visibleDurationSeconds;
        set
        {
            var clamped = Math.Clamp(value, MinVisibleSeconds, MaxVisibleSeconds);
            if (Math.Abs(clamped - _visibleDurationSeconds) > 0.001)
            {
                _visibleDurationSeconds = clamped;
                OnPropertyChanged();
            }
        }
    }

    public double IngressRate
    {
        get => _ingressRate;
        private set
        {
            if (Math.Abs(value - _ingressRate) > 0.001)
            {
                _ingressRate = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsRenderingPaused
    {
        get => _isRenderingPaused;
        set
        {
            if (_isRenderingPaused == value)
            {
                return;
            }

            _isRenderingPaused = value;
            OnPropertyChanged();

            if (!_isRenderingPaused)
            {
                Dispatcher.UIThread.Post(Flush);
            }
        }
    }

    public Array TriggerModes => Enum.GetValues(typeof(TriggerMode));

    public Array TriggerTypes => Enum.GetValues(typeof(TriggerType));

    public Array TriggerSlopes => Enum.GetValues(typeof(TriggerSlope));

    public Array TriggerPolarities => Enum.GetValues(typeof(TriggerPolarity));

    private void OnMetricReceived(MetricSample sample)
    {
        if (IsRenderingPaused)
        {
            if (EnableMetricDetailLogging)
            {
                Logger.LogTrace("Ignored metric {Meter}/{Instrument} because capture is paused", sample.MeterName, sample.InstrumentName);
            }
            return;
        }

        _pending.Enqueue(sample);
        Dispatcher.UIThread.Post(Flush);
        _ingressCounter++;
        if (EnableMetricDetailLogging)
        {
            Logger.LogDebug("Enqueued metric {Meter}/{Instrument} = {Value:0.###} {Unit}", sample.MeterName, sample.InstrumentName, sample.Value, sample.Unit);
        }
    }

    private void OnActivityReceived(ActivitySample sample)
    {
        if (IsRenderingPaused)
        {
            if (EnableMetricDetailLogging)
            {
                Logger.LogTrace("Ignored activity {Name} because capture is paused", sample.Name);
            }
            return;
        }

        _pendingActivities.Enqueue(sample);
        Dispatcher.UIThread.Post(Flush);
        if (EnableMetricDetailLogging)
        {
            Logger.LogDebug("Enqueued activity {Name} duration={Duration:0.###}ms", sample.Name, sample.DurationMilliseconds);
        }
    }

    private void Flush()
    {
        if (EnableMetricDetailLogging)
        {
            Logger.LogTrace("Flush tick");
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Flush);
            return;
        }

        if (IsRenderingPaused)
        {
            if (EnableMetricDetailLogging)
            {
                Logger.LogTrace("Flush skipped because rendering is paused");
            }
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - _retention;
        var hasChanges = false;
        var hasActivityUpdates = false;

        var processed = 0;
        var activitiesProcessed = 0;

        while (_pending.TryDequeue(out var sample))
        {
            if (!double.IsFinite(sample.Value))
            {
                Logger.LogWarning("Discarded non-finite sample {Meter}/{Instrument} ({Value})", sample.MeterName, sample.InstrumentName, sample.Value);
                continue;
            }

            var series = GetOrCreateSeries(sample);
            series.Append(sample.Timestamp, sample.Value, sample.Tags);
            if (EnableMetricDetailLogging)
            {
                Logger.LogDebug("Appended sample {Timestamp:O} -> {Display} ({Value:0.###} {Unit})", sample.Timestamp, series.DisplayName, sample.Value, sample.Unit);
            }
            hasChanges = true;
            processed++;
        }

        foreach (var series in _series)
        {
            series.TrimBefore(cutoff);
        }

        foreach (var activitySeries in _activities)
        {
            activitySeries.TrimBefore(cutoff);
        }

        UpdateIngressRate();

        if (hasChanges)
        {
            OnPropertyChanged(nameof(Series));
        }

        while (_pendingActivities.TryDequeue(out var activity))
        {
            var series = GetOrCreateActivitySeries(activity.Name);
            series.Append(activity);
            hasActivityUpdates = true;
            activitiesProcessed++;
        }

        if (hasActivityUpdates)
        {
            if (EnableMetricDetailLogging)
            {
                Logger.LogTrace("Flush processed {Count} activity samples (activities={ActivitySeries})", activitiesProcessed, _activities.Count);
            }
        }

        if (processed > 0)
        {
            if (EnableMetricDetailLogging)
            {
                Logger.LogTrace("Flush appended {Count} samples (series={SeriesCount})", processed, _series.Count);
                Logger.LogInformation("Flushed {Count} samples. Series tracked: {SeriesCount}. Ingress: {Ingress:F2}/s", processed, _series.Count, IngressRate);
            }
        }
    }

    private void UpdateIngressRate()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastIngressSample;
        if (elapsed >= TimeSpan.FromSeconds(2))
        {
            IngressRate = elapsed.TotalSeconds > 0 ? _ingressCounter / elapsed.TotalSeconds : 0;
            _ingressCounter = 0;
            _lastIngressSample = now;
        }
    }

    private TimelineSeries GetOrCreateSeries(MetricSample sample)
    {
        var key = BuildSeriesKey(sample, out var tagSignature);
        if (_seriesLookup.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var displayName = BuildDisplayName(sample, tagSignature);
        var renderMode = DetermineRenderMode(sample.InstrumentType);
        var series = new TimelineSeries(sample.MeterName, sample.InstrumentName, sample.InstrumentType, displayName, sample.Unit ?? string.Empty, sample.Description ?? string.Empty, tagSignature, renderMode, _palette.Next());
        _seriesLookup[key] = series;
        _series.Add(series);
        if (_triggerConfiguration.TargetSeries is null)
        {
            _triggerConfiguration.TargetSeries = series;
        }
        Logger.LogInformation("Created series for {Meter}/{Instrument} ({Type}) tags: {Tags}", sample.MeterName, sample.InstrumentName, sample.InstrumentType, string.IsNullOrEmpty(tagSignature) ? "<none>" : tagSignature);
        return series;
    }

    private void OnSeriesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_triggerConfiguration.TargetSeries is not null && !_series.Contains(_triggerConfiguration.TargetSeries))
        {
            _triggerConfiguration.TargetSeries = _series.FirstOrDefault();
        }

        if (_triggerConfiguration.TargetSeries is null && _series.Count > 0)
        {
            _triggerConfiguration.TargetSeries = _series[0];
        }
    }

    private static TimelineRenderMode DetermineRenderMode(string instrumentType)
    {
        if (string.IsNullOrWhiteSpace(instrumentType))
        {
            return TimelineRenderMode.Line;
        }

        if (instrumentType.Contains("Counter", StringComparison.OrdinalIgnoreCase))
        {
            return TimelineRenderMode.Step;
        }

        return TimelineRenderMode.Line;
    }

    private static string BuildSeriesKey(MetricSample sample, out string tagSignature)
    {
        tagSignature = BuildTagSignature(sample.Tags);
        return string.IsNullOrWhiteSpace(tagSignature)
            ? $"{sample.MeterName}|{sample.InstrumentName}"
            : $"{sample.MeterName}|{sample.InstrumentName}|{tagSignature}";
    }

    private static string BuildDisplayName(MetricSample sample, string tagSignature)
    {
        if (string.IsNullOrWhiteSpace(tagSignature))
        {
            return $"{sample.MeterName} • {sample.InstrumentName}";
        }

        return $"{sample.MeterName} • {sample.InstrumentName} [{tagSignature}]";
    }

    private void SeedKnownMetricSeries()
    {
        var seeded = false;

        foreach (var seed in SeedMetrics)
        {
            var sample = new MetricSample
            {
                Timestamp = DateTimeOffset.MinValue,
                MeterName = seed.MeterName,
                InstrumentName = seed.InstrumentName,
                InstrumentType = seed.InstrumentType,
                Unit = seed.Unit,
                Description = seed.Description,
                Value = 0,
                ValueType = seed.ValueType,
                Tags = null
            };

            var key = BuildSeriesKey(sample, out _);
            if (_seriesLookup.ContainsKey(key))
            {
                continue;
            }

            GetOrCreateSeries(sample);
            seeded = true;
        }

        if (seeded)
        {
            OnPropertyChanged(nameof(Series));
        }
    }

    private void SeedKnownActivitySeries()
    {
        foreach (var seed in SeedActivities)
        {
            GetOrCreateActivitySeries(seed.Name, seed.Description);
        }
    }

    private ActivitySeries GetOrCreateActivitySeries(string name, string? description = null)
    {
        if (_activitiesLookup.TryGetValue(name, out var existing))
        {
            if (!string.IsNullOrWhiteSpace(description))
            {
                existing.TryUpdateDescription(description);
            }

            return existing;
        }

        var resolvedDescription = description ?? SeedActivities.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
            .Description ?? string.Empty;

        var series = new ActivitySeries(name, resolvedDescription, _activityPalette.Next());
        _activitiesLookup[name] = series;
        _activities.Add(series);
        Logger.LogInformation("Created activity series for {Activity}", name);
        return series;
    }

    private readonly record struct MetricSeed(string MeterName, string InstrumentName, string InstrumentType, string? Unit, string? Description, string ValueType);

    private readonly record struct ActivitySeed(string Name, string Description);

    private static string BuildTagSignature(Dictionary<string, string?>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(tags.Count);
        foreach (var kvp in tags.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            parts.Add(string.IsNullOrWhiteSpace(kvp.Value)
                ? kvp.Key
                : $"{kvp.Key}={kvp.Value}");
        }

        return string.Join(", ", parts);
    }

    public async ValueTask DisposeAsync()
    {
        _flushTimer.Stop();
        _flushTimer.Tick -= _flushHandler;
        _listener.MetricReceived -= OnMetricReceived;
        _listener.ActivityReceived -= OnActivityReceived;
        await _listener.DisposeAsync().ConfigureAwait(false);
        Logger.LogInformation("Metrics dashboard disposed");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? string.Empty));
}
