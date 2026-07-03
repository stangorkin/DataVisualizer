using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class ReportDocumentPageTests
{
    [Fact]
    public void AddPage_BecomesActive()
    {
        var report = new ReportDocument();
        ReportPage first = report.GetOrCreateActivePage();

        ReportPage second = report.AddPage();

        Assert.Equal(2, report.Pages.Count);
        Assert.Equal(second.Id, report.ActivePageId);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void GetOrCreateActivePage_HonorsActivePageId()
    {
        var report = new ReportDocument();
        report.GetOrCreateActivePage();
        ReportPage second = report.AddPage();

        report.SetActivePage(report.Pages[0].Id);
        Assert.Equal(report.Pages[0].Id, report.GetOrCreateActivePage().Id);

        report.SetActivePage(second.Id);
        Assert.Equal(second.Id, report.GetOrCreateActivePage().Id);
    }

    [Fact]
    public void RemovePage_LastPage_IsNotAllowed()
    {
        var report = new ReportDocument();
        ReportPage only = report.GetOrCreateActivePage();

        Assert.False(report.RemovePage(only.Id));
        Assert.Single(report.Pages);
    }

    [Fact]
    public void RemovePage_ActivePage_SelectsNeighbor()
    {
        var report = new ReportDocument();
        report.GetOrCreateActivePage();
        ReportPage second = report.AddPage();
        report.AddPage();

        report.SetActivePage(second.Id);
        Assert.True(report.RemovePage(second.Id));

        Assert.Equal(2, report.Pages.Count);
        Assert.DoesNotContain(report.Pages, p => p.Id == second.Id);
        Assert.Contains(report.Pages, p => p.Id == report.ActivePageId);
    }

    [Fact]
    public void Charts_AreScopedToActivePage()
    {
        var report = new ReportDocument();
        ReportPage first = report.GetOrCreateActivePage();
        first.Visuals.Add(new ChartVisual(
            new ChartSpec(ChartType.Bar, "A", "X", "Y", Aggregation.Sum, [], []),
            ReportVisualLayout.Default));

        report.AddPage();
        Assert.Empty(report.GetOrCreateActivePage().Visuals);

        report.SetActivePage(first.Id);
        Assert.Single(report.GetOrCreateActivePage().Visuals);
    }

    [Fact]
    public void GetOrCreatePageByName_ReusesExistingCaseInsensitive()
    {
        var report = new ReportDocument();
        report.GetOrCreateActivePage();
        ReportPage trends = report.AddPage("Trends");

        ReportPage resolved = report.GetOrCreatePageByName("trends");

        Assert.Equal(trends.Id, resolved.Id);
        Assert.Equal(2, report.Pages.Count); // default page + Trends, no duplicate
    }

    [Fact]
    public void GetOrCreatePageByName_CreatesWhenMissing()
    {
        var report = new ReportDocument();
        report.GetOrCreateActivePage();

        ReportPage created = report.GetOrCreatePageByName("Summary");

        Assert.Equal("Summary", created.Title);
        Assert.Equal(created.Id, report.ActivePageId);
    }

    [Fact]
    public void MovePage_ReordersPages()
    {
        var report = new ReportDocument();
        ReportPage first = report.GetOrCreateActivePage();
        ReportPage second = report.AddPage();
        ReportPage third = report.AddPage();

        Assert.True(report.MovePage(third.Id, 0));

        Assert.Equal(third.Id, report.Pages[0].Id);
        Assert.Equal(first.Id, report.Pages[1].Id);
        Assert.Equal(second.Id, report.Pages[2].Id);
    }

    [Fact]
    public void MovePage_SameIndex_ReturnsFalse()
    {
        var report = new ReportDocument();
        report.GetOrCreateActivePage();
        ReportPage second = report.AddPage();

        Assert.False(report.MovePage(second.Id, 1));
    }
}
