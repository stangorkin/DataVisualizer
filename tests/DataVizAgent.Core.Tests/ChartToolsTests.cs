using DataVizAgent.Ai;
using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class ChartToolsTests
{
    private sealed class StubChartContext : IChartContextProvider
    {
        public SelectedChartContext? SelectedChart { get; set; }
    }

    private static ChartTools CreateTools(out List<ChartSpecResult> captured, out DataService data)
    {
        data = TestData.CreateSalesDataService();
        var results = new List<ChartSpecResult>();
        captured = results;
        return new ChartTools(data, new StubChartContext(), results.Add);
    }

    [Fact]
    public void CreateOrUpdateChart_ValidRequest_ComputesAndRaisesChart()
    {
        var tools = CreateTools(out List<ChartSpecResult> captured, out _);

        string result = tools.CreateOrUpdateChart(
            type: "bar", title: "Sales by Region", xColumn: "Region", yColumn: "Sales", aggregation: "sum");

        Assert.StartsWith("Created", result);
        ChartSpecResult chartResult = Assert.Single(captured);
        Assert.Equal(ChartAction.Create, chartResult.Action);

        ChartSpec chart = chartResult.Chart;
        int westIndex = Array.IndexOf(chart.Labels, "West");
        Assert.True(westIndex >= 0);
        Assert.Equal(250, chart.Values[westIndex]); // 100 + 150
    }

    [Fact]
    public void CreateOrUpdateChart_UnknownColumn_ReturnsErrorAndRaisesNothing()
    {
        var tools = CreateTools(out List<ChartSpecResult> captured, out _);

        string result = tools.CreateOrUpdateChart(
            type: "bar", title: "Bad", xColumn: "NotAColumn", aggregation: "count");

        Assert.Contains("could not be created", result);
        Assert.Empty(captured);
    }

    [Fact]
    public void CreateOrUpdateChart_UpdateAction_IsPreserved()
    {
        var tools = CreateTools(out List<ChartSpecResult> captured, out _);

        tools.CreateOrUpdateChart(
            type: "line", title: "Trend", xColumn: "Year", yColumn: "Sales", aggregation: "sum", action: "update");

        Assert.Equal(ChartAction.Update, Assert.Single(captured).Action);
    }

    [Fact]
    public void RunDataQuery_SumByRegion_ReturnsFormattedResult()
    {
        var tools = CreateTools(out _, out _);

        string result = tools.RunDataQuery(xColumn: "Region", yColumn: "Sales", aggregation: "sum", sort: "desc");

        Assert.Contains("West", result);
        Assert.Contains("250", result);
    }

    [Fact]
    public void Tools_AreDiscoverableByName()
    {
        var tools = CreateTools(out _, out _);

        var names = tools.CreateTools().Select(t => t.Name).ToList();

        Assert.Contains("CreateOrUpdateChart", names);
        Assert.Contains("RunDataQuery", names);
    }
}
