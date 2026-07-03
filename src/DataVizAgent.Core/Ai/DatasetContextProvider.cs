using System.Text;
using DataVizAgent.Models;
using DataVizAgent.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DataVizAgent.Ai;

/// <summary>
/// Supplies the agent's per-turn context: the domain instructions plus a freshly rendered summary
/// of the loaded dataset and the currently selected chart. Implemented as an
/// <see cref="AIContextProvider"/> so the Agent Framework injects it before every run — the model
/// always sees the real schema rather than a remembered (and possibly stale) copy.
/// </summary>
internal sealed class DatasetContextProvider : AIContextProvider
{
    private const string DomainSystemPrompt =
        "You are a data-analysis assistant for a charting app. Use the tools to query the dataset " +
        "and to create or edit charts; never invent column names or numbers. Copy column names " +
        "exactly as listed. When the user just chats, answer conversationally without a tool call.";

    private readonly IDataService _dataService;
    private readonly IChartContextProvider _chartContext;

    public DatasetContextProvider(IDataService dataService, IChartContextProvider chartContext)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _chartContext = chartContext ?? throw new ArgumentNullException(nameof(chartContext));
    }

    protected override ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder(DomainSystemPrompt);

        string datasetContext = BuildDatasetContext();
        if (datasetContext.Length > 0)
        {
            sb.Append("\n\n");
            sb.Append(datasetContext);
        }

        return new ValueTask<AIContext>(new AIContext { Instructions = sb.ToString() });
    }

    private string BuildDatasetContext()
    {
        if (_dataService.RowCount == 0)
            return string.Empty;

        var parts = new List<string>
        {
            _dataService.GetDataSummary(),
            _dataService.GetColumnProfile(),
        };

        SelectedChartContext? selected = _chartContext.SelectedChart;
        if (selected is not null)
        {
            string measure = selected.Aggregation == Aggregation.Count
                ? "row count"
                : $"{selected.Aggregation} of {selected.YColumn}";
            parts.Add(
                $"The user currently has this chart selected: \"{selected.Title}\" — a {selected.Type} chart " +
                $"showing {measure} by {selected.XColumn}. To change it, call the chart tool with action \"update\".");
        }

        return string.Join("\n\n", parts);
    }
}
