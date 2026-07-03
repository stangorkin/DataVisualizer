using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class ChartDataComputerTests
{
    [Fact]
    public void Compute_SumByRegion_AggregatesValues()
    {
        var data = TestData.CreateSalesDataService();
        var request = new ChartSpecRequest { XColumn = "Region", YColumn = "Sales", Aggregation = Aggregation.Sum };

        ChartSpec chart = ChartDataComputer.Compute(request, data);

        int westIndex = Array.IndexOf(chart.Labels, "West");
        Assert.True(westIndex >= 0);
        Assert.Equal(250, chart.Values[westIndex]); // 100 + 150
    }

    [Fact]
    public void Compute_WithFilter_RestrictsRows()
    {
        var data = TestData.CreateSalesDataService();
        var request = new ChartSpecRequest
        {
            XColumn = "Region",
            YColumn = "Sales",
            Aggregation = Aggregation.Sum,
            Filters = [new DataFilter("Year", FilterOperator.Equals, "2024")],
        };

        ChartSpec chart = ChartDataComputer.Compute(request, data);

        int westIndex = Array.IndexOf(chart.Labels, "West");
        Assert.Equal(150, chart.Values[westIndex]); // only the 2024 row
        Assert.NotNull(chart.Filters);
        Assert.Single(chart.Filters!);
    }

    [Fact]
    public void Compute_CarriesReason()
    {
        var data = TestData.CreateSalesDataService();
        var request = new ChartSpecRequest { XColumn = "Region", Aggregation = Aggregation.Count, Reason = "because" };

        ChartSpec chart = ChartDataComputer.Compute(request, data);

        Assert.Equal("because", chart.Reason);
    }
}
