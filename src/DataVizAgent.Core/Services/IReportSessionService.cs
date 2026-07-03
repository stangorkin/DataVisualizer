using DataVizAgent.Models;

namespace DataVizAgent.Services;

public interface IReportSessionService
{
    ReportDocument CurrentReport { get; }
    ChartVisual? SelectedVisual { get; }

    event Action? Changed;

    void StartNewReport(string? datasetName = null, string? title = null);
    void LoadReport(ReportDocument report);
    ChartVisual AddChart(ChartSpec chartSpec);
    IReadOnlyList<ChartVisual> GetCurrentPageCharts();
    void SelectVisual(Guid? visualId);
    bool TryUpdateSelectedChart(ChartVisualDefinition definition, out string? validationError);

    IReadOnlyList<ReportPage> GetPages();
    ReportPage ActivePage { get; }
    void AddPage(string? title = null);
    void SelectPage(Guid pageId);
    bool RenamePage(Guid pageId, string title);
    bool RemovePage(Guid pageId);
    bool MovePage(Guid pageId, int newIndex);
}