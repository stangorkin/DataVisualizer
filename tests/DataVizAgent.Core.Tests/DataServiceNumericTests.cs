using System.Text;
using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class DataServiceNumericTests
{
    private static DataService Load(string csv)
    {
        var service = new DataService(new NullDatasetPersistenceService());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        service.LoadCsvFromStreamAsync(stream, "numeric.csv").GetAwaiter().GetResult();
        return service;
    }

    [Fact]
    public void InferSchema_CurrencyAndPercentColumns_AreNumeric()
    {
        var data = Load(
            "Item,Price,Discount\n" +
            "A,\"$1,299.99\",15%\n" +
            "B,$5,20%\n");

        var schema = data.GetSchema().ToDictionary(c => c.Name, c => c.Type);
        Assert.Equal(ColumnType.Number, schema["Price"]);
        Assert.Equal(ColumnType.Number, schema["Discount"]);
    }

    [Fact]
    public void InferSchema_MostlyNumericPriceColumn_IsNumeric()
    {
        // A real-world price column: mostly money values plus a few non-numeric entries.
        var data = Load(
            "Provider,Price\n" +
            "Alpha,$199\n" +
            "Bravo,Free\n" +
            "Charlie,$299\n" +
            "Delta,Custom\n" +
            "Echo,$99\n");

        Assert.Equal(ColumnType.Number, data.GetSchema().First(c => c.Name == "Price").Type);

        // It can be charted: the numeric prices aggregate and the non-numeric cells are skipped.
        var series = data.QuerySeries("Provider", "Price", Aggregation.Sum);
        Assert.Equal(199, series.First(s => s.Label == "Alpha").Value);
    }

    [Fact]
    public void PriceByProvider_EmptyAndNonNumericCells_RenderAsZero_Generically()
    {
        // Mirrors the real "providers and prices" dataset: clean prices, one mangled value,
        // and providers whose price cell is blank (the "free" rows). Nothing here keys off a
        // specific string like "free" — any non-number where a number is expected is treated
        // the same: skipped from the aggregate, with an all-missing category shown as 0.
        var data = Load(
            "Provider,Price\n" +
            "Tiingo,$30\n" +
            "Finnhub,$199\n" +
            "FinBrain,$499\n" +
            "Acme,Contact us\n" +   // arbitrary non-numeric text
            "Common Crawl,\n" +     // empty cell (a "free" provider)
            "GDELT,\n");            // empty cell

        Assert.Equal(ColumnType.Number, data.GetSchema().First(c => c.Name == "Price").Type);

        var series = data.QuerySeries("Provider", "Price", Aggregation.Sum).ToDictionary(s => s.Label, s => s.Value);
        Assert.Equal(30, series["Tiingo"]);
        Assert.Equal(499, series["FinBrain"]);
        Assert.Equal(0, series["Acme"]);          // non-numeric text → 0
        Assert.Equal(0, series["Common Crawl"]);  // empty → 0
        Assert.Equal(0, series["GDELT"]);
    }

    [Fact]
    public void QuerySeriesWithStats_CountsNonNumericButNotEmptyValues()
    {
        var data = Load(
            "Provider,Price\n" +
            "Tiingo,$30\n" +
            "Alpha,$50 - $2\n" +   // non-numeric (mangled) → counted
            "Beta,Contact us\n" +  // non-numeric text → counted
            "Common Crawl,\n" +    // empty → NOT counted (expected blank)
            "Finnhub,$199\n");

        SeriesResult result = data.QuerySeriesWithStats("Provider", "Price", Aggregation.Sum, []);

        Assert.Equal(2, result.IgnoredNonNumericCount);
    }

    [Fact]
    public void ChartDataComputer_RecordsIgnoredValueCount()
    {
        var data = Load(
            "Provider,Price\n" +
            "Tiingo,$30\n" +
            "Alpha,Contact us\n" +
            "Finnhub,$199\n");

        ChartSpec chart = ChartDataComputer.Compute(
            new ChartSpecRequest { XColumn = "Provider", YColumn = "Price", Aggregation = Aggregation.Sum }, data);

        Assert.Equal(1, chart.IgnoredValueCount);
    }

    [Fact]
    public void QuerySeriesWithStats_CountAggregation_ReportsNoIgnoredValues()
    {
        var data = Load("Provider,Price\nTiingo,$30\nAlpha,Contact us\n");

        SeriesResult result = data.QuerySeriesWithStats("Provider", "Price", Aggregation.Count, []);

        Assert.Equal(0, result.IgnoredNonNumericCount); // Count doesn't read Y
    }

    [Fact]
    public void InferSchema_MostlyTextColumn_StaysString()
    {
        var data = Load(
            "Plan,Seats\n" +
            "Free,one\n" +
            "Pro,5\n" +
            "Team,many\n" +
            "Enterprise,custom\n");

        Assert.Equal(ColumnType.String, data.GetSchema().First(c => c.Name == "Seats").Type);
    }

    [Fact]
    public void InferSchema_IdentifierTextColumn_IsNotNumeric()
    {
        var data = Load(
            "Ref,Amount\n" +
            "Order 1042,10\n" +
            "Order 1043,20\n");

        Assert.Equal(ColumnType.String, data.GetSchema().First(c => c.Name == "Ref").Type);
    }

    [Fact]
    public void QuerySeries_SumParsesCurrencyValues()
    {
        var data = Load(
            "Region,Price\n" +
            "West,\"$1,000\"\n" +
            "West,$250.50\n" +
            "East,$80\n");

        var series = data.QuerySeries("Region", "Price", Aggregation.Sum);

        Assert.Equal(1250.50, series.First(s => s.Label == "West").Value, 2);
    }

    [Fact]
    public void QuerySeries_AverageSkipsBlankAndTextCells()
    {
        var data = Load(
            "Region,Score\n" +
            "West,10\n" +
            "West,\n" +
            "West,n/a\n" +
            "West,20\n");

        var series = data.QuerySeries("Region", "Score", Aggregation.Average);

        // Blank and "n/a" cells are ignored, not averaged in as 0.
        Assert.Equal(15, series.Single().Value);
    }
}
