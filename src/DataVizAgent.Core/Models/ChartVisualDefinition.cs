using DataVizAgent.Services;

namespace DataVizAgent.Models;

public sealed record ChartVisualDefinition(
    ChartType Type,
    string Title,
    string XColumn,
    string YColumn,
    Aggregation Aggregation)
{
    public static ChartVisualDefinition FromChartSpec(ChartSpec chartSpec)
    {
        ArgumentNullException.ThrowIfNull(chartSpec);

        return new ChartVisualDefinition(
            chartSpec.Type,
            chartSpec.Title,
            chartSpec.XColumn,
            chartSpec.YColumn,
            chartSpec.Aggregation);
    }
}