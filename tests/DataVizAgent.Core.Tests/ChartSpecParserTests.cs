using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class ChartSpecParserTests
{
    [Fact]
    public void TryParse_FencedChartBlock_ReturnsRequest()
    {
        const string text = "Here is your chart.\n```chart\n{\"type\":\"bar\",\"xColumn\":\"Region\",\"yColumn\":\"Sales\",\"aggregation\":\"sum\"}\n```";

        var request = ChartSpecParser.TryParse(text);

        Assert.NotNull(request);
        Assert.Equal(ChartType.Bar, request!.Type);
        Assert.Equal("Region", request.XColumn);
        Assert.Equal("Sales", request.YColumn);
        Assert.Equal(Aggregation.Sum, request.Aggregation);
    }

    [Fact]
    public void TryParse_ToolCallWrapper_UnwrapsArguments()
    {
        const string text = "{\"name\":\"create_chart\",\"arguments\":{\"type\":\"pie\",\"xColumn\":\"Region\",\"aggregation\":\"count\"}}";

        var request = ChartSpecParser.TryParse(text);

        Assert.NotNull(request);
        Assert.Equal(ChartType.Pie, request!.Type);
        Assert.Equal("Region", request.XColumn);
        Assert.Equal(Aggregation.Count, request.Aggregation);
    }

    [Fact]
    public void TryParse_BareJsonObject_IsDetected()
    {
        const string text = "Sure! {\"xColumn\":\"Region\",\"yColumn\":\"Sales\",\"aggregation\":\"average\"} done.";

        var request = ChartSpecParser.TryParse(text);

        Assert.NotNull(request);
        Assert.Equal(Aggregation.Average, request!.Aggregation);
    }

    [Fact]
    public void TryParse_ParsesActionAndFilters()
    {
        const string text = "```chart\n{\"xColumn\":\"Region\",\"yColumn\":\"Sales\",\"aggregation\":\"sum\",\"action\":\"update\"," +
            "\"filters\":[{\"column\":\"Year\",\"operator\":\"equals\",\"value\":\"2024\"}]}\n```";

        var request = ChartSpecParser.TryParse(text);

        Assert.NotNull(request);
        Assert.Equal(ChartAction.Update, request!.Action);
        var filter = Assert.Single(request.Filters);
        Assert.Equal("Year", filter.Column);
        Assert.Equal(FilterOperator.Equals, filter.Operator);
        Assert.Equal("2024", filter.Value);
    }

    [Fact]
    public void TryParse_NoChartContent_ReturnsNull()
    {
        Assert.Null(ChartSpecParser.TryParse("Just a friendly message with no chart."));
    }

    [Fact]
    public void TryParse_ParsesTargetPage()
    {
        const string text = "```chart\n{\"xColumn\":\"Region\",\"aggregation\":\"count\",\"page\":\"Trends\"}\n```";

        var request = ChartSpecParser.TryParse(text);

        Assert.NotNull(request);
        Assert.Equal("Trends", request!.Page);
    }

    [Fact]
    public void StripChartBlocks_RemovesChartJson()
    {
        const string text = "Explanation.\n```chart\n{\"xColumn\":\"Region\",\"aggregation\":\"count\"}\n```";

        string stripped = ChartSpecParser.StripChartBlocks(text);

        Assert.Equal("Explanation.", stripped);
    }
}
