using DataVizAgent.Services;

namespace DataVizAgent.Models;

// Table is last so existing persisted sessions (which store the numeric value) stay valid.
public enum ChartType { Bar, Line, Pie, Scatter, Table }

public sealed record ChartSpec(
    ChartType Type,
    string Title,
    string XColumn,
    string YColumn,
    Aggregation Aggregation,
    string[] Labels,
    double[] Values,
    string Reason = "",
    DataFilter[]? Filters = null)
{
    /// <summary>
    /// Number of non-empty Y values that weren't numeric and were excluded from the aggregation.
    /// Surfaced to the user (and the agent) so silently dropped values are transparent.
    /// </summary>
    public int IgnoredValueCount { get; init; }

    /// <summary>Ordering applied to the groups (by value) when the chart was computed.</summary>
    public SortDirection Sort { get; init; } = SortDirection.None;

    /// <summary>Cap applied to the number of groups (0 = all) — "top N" charts record it here.</summary>
    public int Limit { get; init; }
}