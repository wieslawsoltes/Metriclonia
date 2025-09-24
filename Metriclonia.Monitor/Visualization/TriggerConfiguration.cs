using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Metriclonia.Monitor.Visualization;

public enum TriggerMode
{
    Auto,
    Normal
}

public enum TriggerType
{
    Edge,
    Pulse,
    Video,
    Logic,
    Runt,
    Window,
    Pattern,
    Serial,
    Visual
}

public enum TriggerSlope
{
    Rising,
    Falling,
    Either
}

public enum TriggerPolarity
{
    Positive,
    Negative,
    Either
}

public sealed class TriggerConfiguration : INotifyPropertyChanged
{
    private bool _isEnabled;
    private TriggerMode _mode = TriggerMode.Auto;
    private TriggerType _type = TriggerType.Edge;
    private TriggerSlope _slope = TriggerSlope.Rising;
    private TriggerPolarity _polarity = TriggerPolarity.Positive;
    private double _level;
    private double _hysteresis = 0.01;
    private double _windowLow = -1;
    private double _windowHigh = 1;
    private bool _autoLevel = true;
    private double _horizontalPosition = 0.8;
    private double _minimumPulseWidthSeconds = 0.000001;
    private double _maximumPulseWidthSeconds = 1;
    private double _runtLowLevel = 0.3;
    private double _runtHighLevel = 0.7;
    private string _logicPattern = "HL";
    private int _logicSampleLength = 8;
    private string _patternSequence = "0,1";
    private double _patternTolerance = 0.05;
    private int _serialBitCount = 8;
    private double _serialBaudRate = 9600;
    private double _serialThreshold = 0.5;
    private double _videoLineFrequency = 15625; // Hz
    private double _videoTolerancePercentage = 0.1;
    private string _visualTemplate = string.Empty;
    private double _visualTolerance = 0.1;
    private double _holdoffSeconds;
    private bool _freezeOnTrigger;
    private TimelineSeries? _targetSeries;

    internal DateTimeOffset LastResolvedEvent { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }

    public TriggerMode Mode
    {
        get => _mode;
        set => SetField(ref _mode, value);
    }

    public TriggerType Type
    {
        get => _type;
        set => SetField(ref _type, value);
    }

    public TriggerSlope Slope
    {
        get => _slope;
        set => SetField(ref _slope, value);
    }

    public TriggerPolarity Polarity
    {
        get => _polarity;
        set => SetField(ref _polarity, value);
    }

    public bool AutoLevel
    {
        get => _autoLevel;
        set => SetField(ref _autoLevel, value);
    }

    public double HorizontalPosition
    {
        get => _horizontalPosition;
        set => SetField(ref _horizontalPosition, Math.Clamp(value, 0, 1));
    }

    public double Level
    {
        get => _level;
        set => SetField(ref _level, value);
    }

    public double Hysteresis
    {
        get => _hysteresis;
        set => SetField(ref _hysteresis, Math.Max(0, value));
    }

    public double WindowLow
    {
        get => _windowLow;
        set => SetField(ref _windowLow, value);
    }

    public double WindowHigh
    {
        get => _windowHigh;
        set => SetField(ref _windowHigh, value);
    }

    public double MinimumPulseWidthSeconds
    {
        get => _minimumPulseWidthSeconds;
        set => SetField(ref _minimumPulseWidthSeconds, Math.Max(0, value));
    }

    public double MaximumPulseWidthSeconds
    {
        get => _maximumPulseWidthSeconds;
        set => SetField(ref _maximumPulseWidthSeconds, Math.Max(0, value));
    }

    public double RuntLowLevel
    {
        get => _runtLowLevel;
        set => SetField(ref _runtLowLevel, value);
    }

    public double RuntHighLevel
    {
        get => _runtHighLevel;
        set => SetField(ref _runtHighLevel, value);
    }

    public string LogicPattern
    {
        get => _logicPattern;
        set => SetField(ref _logicPattern, value);
    }

    public int LogicSampleLength
    {
        get => _logicSampleLength;
        set => SetField(ref _logicSampleLength, Math.Max(1, value));
    }

    public string PatternSequence
    {
        get => _patternSequence;
        set => SetField(ref _patternSequence, value);
    }

    public double PatternTolerance
    {
        get => _patternTolerance;
        set => SetField(ref _patternTolerance, Math.Max(0, value));
    }

    public int SerialBitCount
    {
        get => _serialBitCount;
        set => SetField(ref _serialBitCount, Math.Clamp(value, 1, 32));
    }

    public double SerialBaudRate
    {
        get => _serialBaudRate;
        set => SetField(ref _serialBaudRate, Math.Max(1, value));
    }

    public double SerialThreshold
    {
        get => _serialThreshold;
        set => SetField(ref _serialThreshold, value);
    }

    public double VideoLineFrequency
    {
        get => _videoLineFrequency;
        set => SetField(ref _videoLineFrequency, Math.Max(1, value));
    }

    public double VideoTolerancePercentage
    {
        get => _videoTolerancePercentage;
        set => SetField(ref _videoTolerancePercentage, Math.Clamp(value, 0, 1));
    }

    public string VisualTemplate
    {
        get => _visualTemplate;
        set => SetField(ref _visualTemplate, value ?? string.Empty);
    }

    public double VisualTolerance
    {
        get => _visualTolerance;
        set => SetField(ref _visualTolerance, Math.Max(0, value));
    }

    public double HoldoffSeconds
    {
        get => _holdoffSeconds;
        set => SetField(ref _holdoffSeconds, Math.Max(0, value));
    }

    public bool FreezeOnTrigger
    {
        get => _freezeOnTrigger;
        set => SetField(ref _freezeOnTrigger, value);
    }

    public TimelineSeries? TargetSeries
    {
        get => _targetSeries;
        set => SetField(ref _targetSeries, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? string.Empty));
}
