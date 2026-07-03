using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class DataQueryTests
{
    [Fact]
    public void TryParse_FencedQueryBlock_ReturnsRequest()
    {
        const string text = "```query\n{\"xColumn\":\"Region\",\"yColumn\":\"Sales\",\"aggregation\":\"sum\",\"sort\":\"desc\",\"limit\":2}\n```";

        var request = DataQueryParser.TryParse(text);

        Assert.NotNull(request);
        Assert.Equal(SortDirection.Desc, request!.Sort);
        Assert.Equal(2, request.Limit);
    }

    [Fact]
    public void Run_SortDescAndLimit_ReturnsTopGroups()
    {
        var data = TestData.CreateSalesDataService();
        var request = new DataQueryRequest
        {
            XColumn = "Region",
            YColumn = "Sales",
            Aggregation = Aggregation.Sum,
            Sort = SortDirection.Desc,
            Limit = 1,
        };

        string result = DataQueryRunner.Run(request, data);

        // West has the highest total (250), so it should be the single returned group.
        Assert.Contains("West", result);
        Assert.Contains("250", result);
        Assert.DoesNotContain("East", result);
    }

    [Fact]
    public void Run_WithFilter_DescribesFilter()
    {
        var data = TestData.CreateSalesDataService();
        var request = new DataQueryRequest
        {
            XColumn = "Region",
            YColumn = "Sales",
            Aggregation = Aggregation.Sum,
            Filters = [new Models.DataFilter("Year", Models.FilterOperator.Equals, "2024")],
        };

        string result = DataQueryRunner.Run(request, data);

        Assert.Contains("Year = 2024", result);
    }

    [Fact]
    public void Run_InvalidColumn_ReturnsErrorText()
    {
        var data = TestData.CreateSalesDataService();
        var request = new DataQueryRequest { XColumn = "Ghost", Aggregation = Aggregation.Count };

        string result = DataQueryRunner.Run(request, data);

        Assert.Contains("could not be run", result);
    }
}
