using DataVizAgent.Models;

namespace DataVizAgent.Services;

public static class ChartDataComputer
{
    public static ChartSpec Compute(ChartSpecRequest request, IDataService dataService)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dataService);

        SeriesResult result = dataService.QuerySeriesWithStats(request.XColumn, request.YColumn, request.Aggregation, request.Filters);

        // "Top N" support: a limit without an explicit sort means top-by-value, which is what
        // "top 5 countries" requests intend; otherwise a limit would just take arbitrary groups.
        SortDirection sort = request.Sort == SortDirection.None && request.Limit > 0
            ? SortDirection.Desc
            : request.Sort;

        IEnumerable<(string Label, double Value)> points = result.Points;
        if (sort == SortDirection.Asc)
            points = points.OrderBy(p => p.Value);
        else if (sort == SortDirection.Desc)
            points = points.OrderByDescending(p => p.Value);
        if (request.Limit > 0)
            points = points.Take(request.Limit);

        var shown = points.ToList();
        return new ChartSpec(
            Type: request.Type,
            Title: request.Title,
            XColumn: request.XColumn,
            YColumn: request.YColumn,
            Aggregation: request.Aggregation,
            Labels: [.. shown.Select(s => s.Label)],
            Values: [.. shown.Select(s => s.Value)],
            Reason: request.Reason,
            Filters: request.Filters.Count > 0 ? [.. request.Filters] : null)
        {
            IgnoredValueCount = result.IgnoredNonNumericCount,
            Sort = sort,
            Limit = request.Limit,
        };
    }
}