using DataVizAgent.Models;

namespace DataVizAgent.Services;

/// <summary>Snapshot of the chart the user currently has selected, shared with the chat agent for context.</summary>
public sealed record SelectedChartContext(
    string Title,
    ChartType Type,
    string XColumn,
    string YColumn,
    Aggregation Aggregation);

/// <summary>
/// Shared holder that lets the chat agent see the user's currently selected chart without creating a
/// circular dependency between <see cref="IChatService"/> and the report session.
/// </summary>
public interface IChartContextProvider
{
    SelectedChartContext? SelectedChart { get; set; }
}

internal sealed class ChartContextProvider : IChartContextProvider
{
    public SelectedChartContext? SelectedChart { get; set; }
}
