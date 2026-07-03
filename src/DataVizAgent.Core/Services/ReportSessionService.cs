using DataVizAgent.Models;

namespace DataVizAgent.Services;

internal sealed class ReportSessionService : IReportSessionService, IDisposable
{
    private readonly IChatService _chatService;
    private readonly IDataService _dataService;
    private readonly IReportPersistenceService _reportPersistenceService;
    private readonly IChartContextProvider _chartContext;
    private Guid? _selectedVisualId;

    public ReportDocument CurrentReport { get; private set; }
    public ChartVisual? SelectedVisual => _selectedVisualId is null
        ? null
        : GetCurrentPageCharts().FirstOrDefault(visual => visual.Id == _selectedVisualId.Value);

    public event Action? Changed;

    public ReportSessionService(
        IChatService chatService,
        IDataService dataService,
        IReportPersistenceService reportPersistenceService,
        IChartContextProvider chartContext)
    {
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _reportPersistenceService = reportPersistenceService ?? throw new ArgumentNullException(nameof(reportPersistenceService));
        _chartContext = chartContext ?? throw new ArgumentNullException(nameof(chartContext));
        _chatService.OnChartSpec += HandleChartSpec;
        CurrentReport = _reportPersistenceService.TryLoadReport() ?? CreateReport();
        CurrentReport.GetOrCreateActivePage();
    }

    public void StartNewReport(string? datasetName = null, string? title = null)
    {
        CurrentReport = CreateReport(datasetName, title);
        _selectedVisualId = null;
        PersistCurrentReport();
        NotifyChanged();
    }

    public void LoadReport(ReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);

        CurrentReport = report;
        CurrentReport.GetOrCreateActivePage();
        _selectedVisualId = null;
        PersistCurrentReport();
        NotifyChanged();
    }

    public ChartVisual AddChart(ChartSpec chartSpec)
    {
        ArgumentNullException.ThrowIfNull(chartSpec);

        ReportPage page = CurrentReport.GetOrCreateActivePage();
        var visual = new ChartVisual(chartSpec, CreateDefaultLayout(page.Visuals.Count));
        page.Visuals.Add(visual);
        _selectedVisualId = visual.Id;
        CurrentReport.Touch();
        PersistCurrentReport();
        NotifyChanged();
        return visual;
    }

    public IReadOnlyList<ChartVisual> GetCurrentPageCharts() => CurrentReport.GetOrCreateActivePage().Visuals;

    public IReadOnlyList<ReportPage> GetPages() => CurrentReport.Pages;

    public ReportPage ActivePage => CurrentReport.GetOrCreateActivePage();

    public void AddPage(string? title = null)
    {
        CurrentReport.AddPage(title);
        _selectedVisualId = null;
        PersistCurrentReport();
        NotifyChanged();
    }

    public void SelectPage(Guid pageId)
    {
        if (!CurrentReport.SetActivePage(pageId))
            return;

        _selectedVisualId = null;
        PersistCurrentReport();
        NotifyChanged();
    }

    public bool RenamePage(Guid pageId, string title)
    {
        ReportPage? page = CurrentReport.Pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null || string.IsNullOrWhiteSpace(title))
            return false;

        page.Title = title.Trim();
        CurrentReport.Touch();
        PersistCurrentReport();
        NotifyChanged();
        return true;
    }

    public bool RemovePage(Guid pageId)
    {
        if (!CurrentReport.RemovePage(pageId))
            return false;

        _selectedVisualId = null;
        PersistCurrentReport();
        NotifyChanged();
        return true;
    }

    public bool MovePage(Guid pageId, int newIndex)
    {
        if (!CurrentReport.MovePage(pageId, newIndex))
            return false;

        PersistCurrentReport();
        NotifyChanged();
        return true;
    }

    public void SelectVisual(Guid? visualId)
    {
        if (visualId is null)
        {
            if (_selectedVisualId is null)
                return;

            _selectedVisualId = null;
            NotifyChanged();
            return;
        }

        if (!GetCurrentPageCharts().Any(visual => visual.Id == visualId.Value))
            return;

        if (_selectedVisualId == visualId.Value)
            return;

        _selectedVisualId = visualId.Value;
        NotifyChanged();
    }

    public bool TryUpdateSelectedChart(ChartVisualDefinition definition, out string? validationError)
    {
        ArgumentNullException.ThrowIfNull(definition);

        validationError = null;
        ChartVisual? visual = SelectedVisual;
        if (visual is null)
        {
            validationError = "Select a chart to edit.";
            return false;
        }

        if (_dataService.RowCount == 0)
        {
            validationError = "Load a dataset before editing a chart.";
            return false;
        }

        string title = string.IsNullOrWhiteSpace(definition.Title)
            ? "Untitled Chart"
            : definition.Title.Trim();
        string xColumn = definition.XColumn.Trim();
        string yColumn = definition.YColumn.Trim();

        if (!HasColumn(xColumn))
        {
            validationError = "Select a valid X column.";
            return false;
        }

        if (RequiresNumericYColumn(definition.Aggregation))
        {
            ColumnInfo? yColumnInfo = GetColumn(yColumn);
            if (yColumnInfo is null)
            {
                validationError = "Select a valid Y column.";
                return false;
            }

            if (yColumnInfo.Type != ColumnType.Number)
            {
                validationError = "Select a numeric Y column for this aggregation.";
                return false;
            }
        }

        var request = new ChartSpecRequest
        {
            Type = definition.Type,
            Title = title,
            XColumn = xColumn,
            YColumn = yColumn,
            Aggregation = definition.Aggregation,
        };

        ChartSpec updatedChart = ChartDataComputer.Compute(request, _dataService);
        visual.UpdateChart(updatedChart);
        CurrentReport.Touch();
        PersistCurrentReport();
        NotifyChanged();
        return true;
    }

    public void Dispose()
    {
        _chatService.OnChartSpec -= HandleChartSpec;
    }

    private void HandleChartSpec(ChartSpecResult result)
    {
        // An explicit update of the selected chart wins over page targeting — "change this
        // chart" should never silently create a duplicate on another page.
        if (result.Action == ChartAction.Update && SelectedVisual is not null)
        {
            UpdateSelectedChart(result.Chart);
            return;
        }

        // If the agent named a target page, switch to it (creating it when needed) before placing the chart.
        if (!string.IsNullOrWhiteSpace(result.TargetPage))
        {
            ReportPage page = CurrentReport.GetOrCreatePageByName(result.TargetPage);
            CurrentReport.SetActivePage(page.Id);
            _selectedVisualId = null;
        }

        AddChart(result.Chart);
    }

    private void UpdateSelectedChart(ChartSpec chartSpec)
    {
        ChartVisual? visual = SelectedVisual;
        if (visual is null)
        {
            AddChart(chartSpec);
            return;
        }

        visual.UpdateChart(chartSpec);
        CurrentReport.Touch();
        PersistCurrentReport();
        NotifyChanged();
    }

    private bool HasColumn(string columnName) => GetColumn(columnName) is not null;

    private ColumnInfo? GetColumn(string columnName) =>
        string.IsNullOrWhiteSpace(columnName)
            ? null
            : _dataService.GetSchema().FirstOrDefault(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase));

    private static bool RequiresNumericYColumn(Aggregation aggregation) => aggregation != Aggregation.Count;

    private static ReportDocument CreateReport(string? datasetName = null, string? title = null)
    {
        string resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? "Untitled Report"
            : title.Trim();

        var report = new ReportDocument
        {
            Title = resolvedTitle,
            DatasetName = string.IsNullOrWhiteSpace(datasetName) ? null : datasetName.Trim(),
        };

        report.GetOrCreateActivePage();
        return report;
    }

    private static ReportVisualLayout CreateDefaultLayout(int visualIndex)
    {
        int column = visualIndex % 2;
        int row = visualIndex / 2;
        return new ReportVisualLayout(column * 6, row * 5, 6, 5);
    }

    private void PersistCurrentReport() => _reportPersistenceService.SaveReport(CurrentReport);

    private void NotifyChanged()
    {
        SyncChartContext();
        Changed?.Invoke();
    }

    private void SyncChartContext()
    {
        ChartVisual? visual = SelectedVisual;
        _chartContext.SelectedChart = visual is null
            ? null
            : new SelectedChartContext(
                visual.Chart.Title,
                visual.Chart.Type,
                visual.Chart.XColumn,
                visual.Chart.YColumn,
                visual.Chart.Aggregation);
    }
}