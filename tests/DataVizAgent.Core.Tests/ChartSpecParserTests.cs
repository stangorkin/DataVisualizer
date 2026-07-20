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

    [Fact]
    public void TryParse_UnknownEnumValue_FallsBackInsteadOfFailing()
    {
        // A model inventing an unsupported option must not discard the whole tool call —
        // that is how raw JSON blocks end up displayed in chat.
        const string text = "```chart\n{\"type\":\"histogram\",\"xColumn\":\"Region\",\"aggregation\":\"count\"}\n```";

        var request = ChartSpecParser.TryParse(text);

        Assert.NotNull(request);
        Assert.Equal(ChartType.Bar, request!.Type);
        Assert.Equal("Region", request.XColumn);
        Assert.Equal(Aggregation.Count, request.Aggregation);
    }

    [Fact]
    public void TryParse_TableType_IsSupported()
    {
        const string text = "```chart\n{\"type\":\"table\",\"xColumn\":\"Region\",\"aggregation\":\"count\",\"sort\":\"desc\",\"limit\":10}\n```";

        var request = ChartSpecParser.TryParse(text);

        Assert.NotNull(request);
        Assert.Equal(ChartType.Table, request!.Type);
        Assert.Equal(SortDirection.Desc, request.Sort);
        Assert.Equal(10, request.Limit);
    }

    [Fact]
    public void StripChartBlocks_RemovesMalformedChartTaggedBlock()
    {
        const string text = "Here you go.\n```chart\n{\"type\": \"bar\", not valid json}\n```";

        string stripped = ChartSpecParser.StripChartBlocks(text);

        Assert.Equal("Here you go.", stripped);
    }

    [Fact]
    public void ContainsToolBlock_DetectsCompleteTaggedBlocks()
    {
        Assert.True(ChartSpecParser.ContainsToolBlock("```chart\n{\"x\":1}\n```"));
        Assert.True(ChartSpecParser.ContainsToolBlock("```query\n{\"x\":1}\n```"));
        Assert.False(ChartSpecParser.ContainsToolBlock("plain prose"));
        Assert.False(ChartSpecParser.ContainsToolBlock("```chart\n{\"x\": truncated"));
    }

    [Fact]
    public void TryStripUnclosedFencedBlock_RemovesTruncatedChartBlock()
    {
        // Shape observed when generation hits the token cap mid tool call.
        const string text = "Here is the chart.\n```chart\n{\"type\": \"bar\", \"title\": \"Top 5 Most Reported Countries\", \"reason\": \"To show the";

        bool stripped = ChartSpecParser.TryStripUnclosedFencedBlock(text, out string cleaned);

        Assert.True(stripped);
        Assert.Equal("Here is the chart.", cleaned);
    }

    [Fact]
    public void TryStripUnclosedFencedBlock_TruncatedBlockOnly_LeavesEmptyText()
    {
        const string text = "```chart\n{\"type\": \"bar\", \"xColumn\": \"colu";

        bool stripped = ChartSpecParser.TryStripUnclosedFencedBlock(text, out string cleaned);

        Assert.True(stripped);
        Assert.Equal(string.Empty, cleaned);
    }

    [Fact]
    public void TryStripUnclosedFencedBlock_CompleteBlock_IsUntouched()
    {
        const string text = "Done.\n```chart\n{\"xColumn\":\"Region\",\"aggregation\":\"count\"}\n```";

        bool stripped = ChartSpecParser.TryStripUnclosedFencedBlock(text, out string cleaned);

        Assert.False(stripped);
        Assert.Equal(text, cleaned);
    }
}
