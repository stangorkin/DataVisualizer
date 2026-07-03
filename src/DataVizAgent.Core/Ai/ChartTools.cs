using System.ComponentModel;
using DataVizAgent.Models;
using DataVizAgent.Services;
using Microsoft.Extensions.AI;

namespace DataVizAgent.Ai;

/// <summary>
/// Exposes the chart and data-query operations as Microsoft.Extensions.AI <see cref="AIFunction"/>
/// tools. This is the payoff of the IChatClient refactor: the hand-written response scraping in
/// <c>ChartSpecParser</c> / <c>DataQueryParser</c> is replaced by typed methods whose parameter
/// schemas are generated from these signatures, and validation flows through the same
/// <see cref="ChartSpecValidator"/> the manual editor uses.
/// </summary>
internal sealed class ChartTools
{
    private readonly IDataService _dataService;
    private readonly IChartContextProvider _chartContext;
    private readonly Action<ChartSpecResult> _onChart;

    public ChartTools(IDataService dataService, IChartContextProvider chartContext, Action<ChartSpecResult> onChart)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _chartContext = chartContext ?? throw new ArgumentNullException(nameof(chartContext));
        _onChart = onChart ?? throw new ArgumentNullException(nameof(onChart));
    }

    public IList<AITool> CreateTools() =>
    [
        AIFunctionFactory.Create(CreateOrUpdateChart),
        AIFunctionFactory.Create(RunDataQuery),
    ];

    [Description("Create a new chart, or update the chart the user currently has selected, from the loaded dataset.")]
    public string CreateOrUpdateChart(
        [Description("Chart type: bar, line, pie, or scatter.")] string type,
        [Description("Short chart title.")] string title,
        [Description("Exact name of the column for the X axis (categories).")] string xColumn,
        [Description("Exact name of the numeric column for the Y axis. Omit for a count aggregation.")] string yColumn = "",
        [Description("Aggregation: none, sum, average, count, min, or max.")] string aggregation = "count",
        [Description("Use 'create' to add a chart, or 'update' to change the selected chart.")] string action = "create",
        [Description("Optional report page name to place the chart on; created if it does not exist.")] string page = "",
        [Description("One short sentence explaining why this chart answers the request.")] string reason = "")
    {
        if (_dataService.RowCount == 0)
            return "No dataset is loaded. Ask the user to load a CSV, Excel, or JSON file first.";

        var request = new ChartSpecRequest
        {
            Type = ParseEnum(type, ChartType.Bar),
            Title = title ?? string.Empty,
            XColumn = xColumn ?? string.Empty,
            YColumn = yColumn ?? string.Empty,
            Aggregation = ParseEnum(aggregation, Aggregation.Count),
            Action = ParseEnum(action, ChartAction.Create),
            Page = page ?? string.Empty,
            Reason = reason ?? string.Empty,
        };

        ChartSpecValidationResult validation = ChartSpecValidator.Validate(request, _dataService);
        if (!validation.IsValid)
            return $"The chart could not be created because {validation.Error}";

        ChartSpecRequest normalized = validation.Normalized!;
        ChartSpec chart = ChartDataComputer.Compute(normalized, _dataService);
        string? targetPage = string.IsNullOrWhiteSpace(normalized.Page) ? null : normalized.Page;

        _onChart(new ChartSpecResult(chart, normalized.Action, targetPage));

        string verb = normalized.Action == ChartAction.Update ? "Updated" : "Created";
        string ignoredNote = chart.IgnoredValueCount > 0
            ? $" Note: {chart.IgnoredValueCount} non-numeric value(s) in \"{normalized.YColumn}\" were ignored — mention this to the user."
            : string.Empty;
        return $"{verb} a {normalized.Type} chart titled \"{normalized.Title}\" with {chart.Labels.Length} data points.{ignoredNote} " +
               "Briefly tell the user what the chart shows.";
    }

    [Description("Compute an aggregation over the dataset and return the grouped values so you can answer an analytical question in prose. Use this instead of guessing numbers.")]
    public string RunDataQuery(
        [Description("Exact name of the column to group by.")] string xColumn,
        [Description("Exact name of the numeric column to aggregate. Omit for a count.")] string yColumn = "",
        [Description("Aggregation: none, sum, average, count, min, or max.")] string aggregation = "count",
        [Description("Sort of the grouped results: none, asc, or desc.")] string sort = "none",
        [Description("Maximum number of groups to return; 0 for all.")] int limit = 0)
    {
        if (_dataService.RowCount == 0)
            return "No dataset is loaded. Ask the user to load a CSV, Excel, or JSON file first.";

        var request = new DataQueryRequest
        {
            XColumn = xColumn ?? string.Empty,
            YColumn = yColumn ?? string.Empty,
            Aggregation = ParseEnum(aggregation, Aggregation.Count),
            Sort = ParseEnum(sort, SortDirection.None),
            Limit = limit,
        };

        return DataQueryRunner.Run(request, _dataService);
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out TEnum parsed) ? parsed : fallback;
}
