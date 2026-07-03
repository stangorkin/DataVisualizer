using DataVizAgent.Models;

namespace DataVizAgent.Services;

public static class ChartDataComputer
{
    public static ChartSpec Compute(ChartSpecRequest request, IDataService dataService)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dataService);

        SeriesResult result = dataService.QuerySeriesWithStats(request.XColumn, request.YColumn, request.Aggregation, request.Filters);
        return new ChartSpec(
            Type: request.Type,
            Title: request.Title,
            XColumn: request.XColumn,
            YColumn: request.YColumn,
            Aggregation: request.Aggregation,
            Labels: [.. result.Points.Select(s => s.Label)],
            Values: [.. result.Points.Select(s => s.Value)],
            Reason: request.Reason,
            Filters: request.Filters.Count > 0 ? [.. request.Filters] : null)
        {
            IgnoredValueCount = result.IgnoredNonNumericCount,
        };
    }
}