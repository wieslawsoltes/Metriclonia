using System;
using System.Collections.Generic;

namespace Metriclonia.Monitor.Visualization;

public sealed class FlameChartDefinition
{
    public FlameChartDefinition(string title, IReadOnlyList<FlameLaneDefinition> lanes)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Lanes = lanes ?? throw new ArgumentNullException(nameof(lanes));
    }

    public string Title { get; }

    public IReadOnlyList<FlameLaneDefinition> Lanes { get; }
}

public sealed class FlameLaneDefinition
{
    public FlameLaneDefinition(string displayName, FlameLaneSourceType sourceType, string sourceKey, string? description = null)
    {
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        SourceType = sourceType;
        SourceKey = sourceKey ?? throw new ArgumentNullException(nameof(sourceKey));
        Description = description;
    }

    public string DisplayName { get; }

    public FlameLaneSourceType SourceType { get; }

    public string SourceKey { get; }

    public string? Description { get; }
}

public enum FlameLaneSourceType
{
    Metric,
    Activity
}
