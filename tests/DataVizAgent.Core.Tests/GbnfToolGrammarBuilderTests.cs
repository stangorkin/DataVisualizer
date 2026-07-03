using DataVizAgent.Ai;
using DataVizAgent.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class GbnfToolGrammarBuilderTests
{
    private sealed class StubChartContext : IChartContextProvider
    {
        public SelectedChartContext? SelectedChart { get; set; }
    }

    private static IReadOnlyList<AIFunction> ChartToolFunctions()
    {
        var data = TestData.CreateSalesDataService();
        var tools = new ChartTools(data, new StubChartContext(), _ => { });
        return [.. tools.CreateTools().OfType<AIFunction>()];
    }

    [Fact]
    public void TryBuild_ForChartTools_EmitsRootToolNamesAndPrimitiveRules()
    {
        string? grammar = GbnfToolGrammarBuilder.TryBuild(ChartToolFunctions());

        Assert.NotNull(grammar);
        Assert.Contains("root ::=", grammar);
        Assert.Contains("toolcall ::=", grammar);
        // Both tool names appear as exact-match literals in the grammar.
        Assert.Contains("CreateOrUpdateChart", grammar);
        Assert.Contains("RunDataQuery", grammar);
        // Shared primitive rules are defined.
        Assert.Contains("str ::=", grammar);
        Assert.Contains("int ::=", grammar);
    }

    [Fact]
    public void TryBuild_ConstrainsIntegerParameterToIntRule()
    {
        // RunDataQuery.limit is an int; its key must be present and the int rule defined.
        string? grammar = GbnfToolGrammarBuilder.TryBuild(ChartToolFunctions());

        Assert.NotNull(grammar);
        Assert.Contains("limit", grammar);
        Assert.Contains("int ::= \"-\"? [0-9]+", grammar);
    }

    [Fact]
    public void TryBuild_WithUnsupportedArrayParameter_ReturnsNull()
    {
        AIFunction arrayTool = AIFunctionFactory.Create((string[] tags) => "ok", "array_tool", "takes an array");

        Assert.Null(GbnfToolGrammarBuilder.TryBuild([arrayTool]));
    }

    [Fact]
    public void TryBuild_WithNoTools_ReturnsNull()
    {
        Assert.Null(GbnfToolGrammarBuilder.TryBuild([]));
    }
}
