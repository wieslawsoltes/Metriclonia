using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace Metriclonia.Producer.ViewModels;

public sealed class MainWindowViewModel
{
    private const int TimelineCapacity = 240;

    private readonly Random _random = new();
    private readonly DispatcherTimer _updateTimer;

    public ObservableCollection<ProjectNode> Projects { get; } = new();

    public ObservableCollection<SummaryMetric> Summary { get; } = new();

    public ObservableCollection<TaskItem> Tasks { get; } = new();

    public ObservableCollection<ActivityLogItem> ActivityLog { get; } = new();

    public ObservableCollection<double> RenderTimeline { get; } = new();

    public ObservableCollection<double> LayoutTimeline { get; } = new();

    public MainWindowViewModel()
    {
        SeedProjects();
        SeedSummary();
        SeedTasks();
        SeedTimelines();

        _updateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) => UpdateModel());
        _updateTimer.Start();
    }

    private void SeedProjects()
    {
        Projects.Add(new ProjectNode("Design System")
        {
            Summary = "Refining application theming and fluent resources",
            Children =
            {
                new("Dark Theme") { Summary = "Refactor accent discovery pipeline" },
                new("Light Theme") { Summary = "Add density presets" },
                new("High Contrast") { Summary = "Adapt for WAI-ARIA palette" }
            }
        });

        Projects.Add(new ProjectNode("Telemetry")
        {
            Summary = "Visualizing runtime metrics",
            Children =
            {
                new("UDP transport") { Summary = "Publish measurements to observers" },
                new("Timeline view") { Summary = "Zoom and pan real time charts" },
                new("Aggregation") { Summary = "Summaries for render vs layout" }
            }
        });

        Projects.Add(new ProjectNode("Rendering Experiments")
        {
            Summary = "Particle fields and shape tessellation",
            Children =
            {
                new("Particle Playground") { Summary = "Stress composition thread" },
                new("Bezier Scene") { Summary = "Interactive geometry" },
                new("Shader Graph") { Summary = "Custom fragment brushes" }
            }
        });
    }

    private void SeedSummary()
    {
        Summary.Add(new SummaryMetric("UI Render", "ms", volatility: 2.5) { Value = 6.5 });
        Summary.Add(new SummaryMetric("Layout Measure", "ms", volatility: 1.8) { Value = 4.1 });
        Summary.Add(new SummaryMetric("Visual Tree", "items", volatility: 14) { Value = 182 });
        Summary.Add(new SummaryMetric("Dispatcher Timers", "count", volatility: 2) { Value = 5 });
    }

    private void SeedTasks()
    {
        var owners = new[] { "Ava", "Noah", "Maya", "Kai", "Raven" };
        var states = new[] { "Pending", "In Progress", "Review", "Done" };

        for (var i = 0; i < 12; i++)
        {
            Tasks.Add(new TaskItem
            {
                Title = $"Experiment #{i + 1:00}",
                Owner = owners[_random.Next(owners.Length)],
                Progress = Math.Round(_random.NextDouble(), 2),
                State = states[_random.Next(states.Length)],
                DueDate = DateTime.Today.AddDays(_random.Next(3, 40))
            });
        }
    }

    private void SeedTimelines()
    {
        foreach (var _ in Enumerable.Range(0, TimelineCapacity))
        {
            RenderTimeline.Add(NextRenderSample());
            LayoutTimeline.Add(NextLayoutSample());
        }
    }

    private void UpdateModel()
    {
        foreach (var item in Summary)
        {
            var delta = (NextGaussian() - 0.5) * item.Volatility;
            item.Value = Math.Max(0, item.Value + delta);
            item.Trend = delta;
        }

        if (Tasks.Count > 0)
        {
            var index = _random.Next(Tasks.Count);
            var task = Tasks[index];
            var drift = Math.Clamp(task.Progress + (_random.NextDouble() - 0.45) * 0.1, 0, 1);
            task.Progress = Math.Round(drift, 2);
            task.State = task.Progress > 0.95 ? "Done" : task.Progress > 0.6 ? "Review" : task.State;
        }

        PushTimeline(RenderTimeline, NextRenderSample());
        PushTimeline(LayoutTimeline, NextLayoutSample());

        ActivityLog.Insert(0, new ActivityLogItem
        {
            Timestamp = DateTimeOffset.Now,
            Message = $"Metric refresh {RenderTimeline[^1]:0.00} ms render / {LayoutTimeline[^1]:0.00} ms layout",
            Category = _random.NextDouble() > 0.6 ? "Render" : "Layout"
        });

        while (ActivityLog.Count > 40)
        {
            ActivityLog.RemoveAt(ActivityLog.Count - 1);
        }
    }

    private void PushTimeline(ObservableCollection<double> target, double value)
    {
        if (target.Count >= TimelineCapacity)
        {
            target.RemoveAt(0);
        }

        target.Add(value);
    }

    private double NextRenderSample()
        => Math.Max(0.5, 5 + NextGaussian() * 1.5);

    private double NextLayoutSample()
        => Math.Max(0.2, 4 + NextGaussian() * 1.0);

    private double NextGaussian()
    {
        var u1 = 1.0 - _random.NextDouble();
        var u2 = 1.0 - _random.NextDouble();
        var standardNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        return 0.5 + standardNormal * 0.25;
    }
}

public sealed class ProjectNode
{
    public ProjectNode(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public string Summary { get; set; } = string.Empty;

    public ObservableCollection<ProjectNode> Children { get; set; } = new();
}

public sealed class SummaryMetric : INotifyPropertyChanged
{
    private double _value;
    private double _trend;

    public SummaryMetric(string name, string unit, double volatility)
    {
        Name = name;
        Unit = unit;
        Volatility = volatility;
    }

    public string Name { get; }

    public string Unit { get; }

    public double Volatility { get; }

    public double Value
    {
        get => _value;
        set
        {
        if (Math.Abs(value - _value) > 0.0001)
            {
                _value = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayValue));
            }
        }
    }

    public double Trend
    {
        get => _trend;
        set
        {
        if (Math.Abs(value - _trend) > 0.0001)
            {
                _trend = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TrendText));
            }
        }
    }

    public string DisplayValue => $"{Value:0.00} {Unit}";

    public string TrendText => Trend >= 0 ? $"+{Trend:0.00}" : $"{Trend:0.00}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class TaskItem : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _owner = string.Empty;
    private double _progress;
    private string _state = string.Empty;
    private DateTime _dueDate = DateTime.Today;

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    public string Owner
    {
        get => _owner;
        set
        {
            if (_owner != value)
            {
                _owner = value;
                OnPropertyChanged();
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
        if (Math.Abs(value - _progress) > 0.0001)
            {
                _progress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }
    }

    public string ProgressPercent => $"{Progress * 100:0}%";

    public string State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime DueDate
    {
        get => _dueDate;
        set
        {
            if (_dueDate != value)
            {
                _dueDate = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ActivityLogItem
{
    public DateTimeOffset Timestamp { get; set; }

    public string Message { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;
}
