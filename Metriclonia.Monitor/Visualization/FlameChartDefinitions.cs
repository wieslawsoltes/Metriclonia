using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Metriclonia.Monitor.Visualization;

public static class FlameChartDefinitions
{
    public static readonly FlameChartDefinition Rendering = new(
        "Rendering Pipeline",
        new List<FlameLaneDefinition>
        {
            new("Compositor render (RT)", FlameLaneSourceType.Metric, "avalonia.comp.render.time", "Render thread GPU submission duration"),
            new("Compositor update (RT)", FlameLaneSourceType.Metric, "avalonia.comp.update.time", "Render thread update pass work"),
            new("UI render (UI)", FlameLaneSourceType.Metric, "avalonia.ui.render.time", "UI thread scene graph recording")
        });

    public static readonly FlameChartDefinition Layout = new(
        "Layout",
        new List<FlameLaneDefinition>
        {
            new("Layout measure (metric)", FlameLaneSourceType.Metric, "avalonia.ui.measure.time", "UI thread measure pass durations"),
            new("Layout arrange (metric)", FlameLaneSourceType.Metric, "avalonia.ui.arrange.time", "UI thread arrange pass durations"),
            new("Measuring layoutables", FlameLaneSourceType.Activity, "Avalonia.MeasuringLayoutable", "Activity events for measure overrides"),
            new("Arranging layoutables", FlameLaneSourceType.Activity, "Avalonia.ArrangingLayoutable", "Activity events for arrange overrides")
        });

    public static readonly FlameChartDefinition Input = new(
        "Input Pipeline",
        new List<FlameLaneDefinition>
        {
            new("Input processing (metric)", FlameLaneSourceType.Metric, "avalonia.ui.input.time", "UI thread input dispatch duration"),
            new("Hit testing", FlameLaneSourceType.Activity, "Avalonia.PerformingHitTest", "Hit test traversal for input"),
            new("Routed events", FlameLaneSourceType.Activity, "Avalonia.RaisingRoutedEvent", "Routed input event processing")
        });

    public static readonly FlameChartDefinition Resources = new(
        "Resources & Styling",
        new List<FlameLaneDefinition>
        {
            new("Resource lookups", FlameLaneSourceType.Activity, "Avalonia.FindingResource", "Resource resolution latencies"),
            new("Style attachment", FlameLaneSourceType.Activity, "Avalonia.AttachingStyle", "Applying styles to controls"),
            new("Style evaluation", FlameLaneSourceType.Activity, "Avalonia.EvaluatingStyle", "Evaluating style activators and setters")
        });

    public static readonly IReadOnlyList<FlameChartDefinition> All = new ReadOnlyCollection<FlameChartDefinition>(
        new[] { Rendering, Layout, Input, Resources });
}
