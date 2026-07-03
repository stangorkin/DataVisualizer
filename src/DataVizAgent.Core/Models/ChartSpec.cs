using DataVizAgent.Services;

namespace DataVizAgent.Models;

public enum ChartType { Bar, Line, Pie, Scatter }

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
}