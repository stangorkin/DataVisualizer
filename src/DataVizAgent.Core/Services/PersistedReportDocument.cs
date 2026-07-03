using DataVizAgent.Models;

namespace DataVizAgent.Services;

public sealed class PersistedReportDocument
{
    public string Title { get; init; } = "Untitled Report";
    public string? DatasetName { get; init; }
    public Guid? ActivePageId { get; init; }
    public List<PersistedReportPage> Pages { get; init; } = [];

    public static PersistedReportDocument FromReportDocument(ReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new PersistedReportDocument
        {
            Title = report.Title,
            DatasetName = report.DatasetName,
            ActivePageId = report.ActivePageId,
            Pages = [.. report.Pages.Select(PersistedReportPage.FromReportPage)],
        };
    }

    public ReportDocument ToReportDocument()
    {
        var report = new ReportDocument
        {
            Title = string.IsNullOrWhiteSpace(Title) ? "Untitled Report" : Title.Trim(),
            DatasetName = string.IsNullOrWhiteSpace(DatasetName) ? null : DatasetName.Trim(),
        };

        foreach (PersistedReportPage persistedPage in Pages)
            report.Pages.Add(persistedPage.ToReportPage());

        if (ActivePageId is not null && report.Pages.Any(p => p.Id == ActivePageId.Value))
            report.ActivePageId = ActivePageId;

        report.GetOrCreateActivePage();
        return report;
    }
}

public sealed class PersistedReportPage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "Page 1";
    public List<PersistedChartVisual> Visuals { get; init; } = [];

    public static PersistedReportPage FromReportPage(ReportPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new PersistedReportPage
        {
            Id = page.Id,
            Title = page.Title,
            Visuals = [.. page.Visuals.Select(PersistedChartVisual.FromChartVisual)],
        };
    }

    public ReportPage ToReportPage()
    {
        var page = new ReportPage
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Title = string.IsNullOrWhiteSpace(Title) ? "Page 1" : Title.Trim(),
        };

        foreach (PersistedChartVisual persistedVisual in Visuals)
            page.Visuals.Add(persistedVisual.ToChartVisual());

        return page;
    }
}

public sealed class PersistedChartVisual
{
    public ChartSpec Chart { get; init; } = new(
        ChartType.Bar,
        "Untitled Chart",
        string.Empty,
        string.Empty,
        Aggregation.None,
        [],
        []);

    public ReportVisualLayout Layout { get; init; } = ReportVisualLayout.Default;

    public static PersistedChartVisual FromChartVisual(ChartVisual visual)
    {
        ArgumentNullException.ThrowIfNull(visual);

        return new PersistedChartVisual
        {
            Chart = visual.Chart,
            Layout = visual.Layout,
        };
    }

    public ChartVisual ToChartVisual() => new(Chart, Layout);
}