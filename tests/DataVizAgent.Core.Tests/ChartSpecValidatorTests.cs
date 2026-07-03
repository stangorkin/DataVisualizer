using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class ChartSpecValidatorTests
{
    [Fact]
    public void Validate_NormalizesColumnCasing()
    {
        var data = TestData.CreateSalesDataService();
        var request = new ChartSpecRequest { XColumn = "region", YColumn = "sales", Aggregation = Aggregation.Sum };

        var result = ChartSpecValidator.Validate(request, data);

        Assert.True(result.IsValid);
        Assert.Equal("Region", result.Normalized!.XColumn);
        Assert.Equal("Sales", result.Normalized.YColumn);
    }

    [Fact]
    public void Validate_UnknownXColumn_Fails()
    {
        var data = TestData.CreateSalesDataService();
        var request = new ChartSpecRequest { XColumn = "Nope", Aggregation = Aggregation.Count };

        var result = ChartSpecValidator.Validate(request, data);

        Assert.False(result.IsValid);
        Assert.Contains("Nope", result.Error);
    }

    [Fact]
    public void Validate_NonNumericYForSum_Fails()
    {
        var data = TestData.CreateSalesDataService();
        var request = new ChartSpecRequest { XColumn = "Region", YColumn = "Customer", Aggregation = Aggregation.Sum };

        var result = ChartSpecValidator.Validate(request, data);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_CountDoesNotRequireYColumn()
    {
        var data = TestData.CreateSalesDataService();
        var request = new ChartSpecRequest { XColumn = "Region", Aggregation = Aggregation.Count };

        var result = ChartSpecValidator.Validate(request, data);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_DropsFilterWithUnknownColumn()
    {
        var data = TestData.CreateSalesDataService();
        var request = new ChartSpecRequest
        {
            XColumn = "Region",
            Aggregation = Aggregation.Count,
            Filters = [new DataFilter("Ghost", FilterOperator.Equals, "x"), new DataFilter("year", FilterOperator.Equals, "2024")],
        };

        var result = ChartSpecValidator.Validate(request, data);

        Assert.True(result.IsValid);
        var filter = Assert.Single(result.Normalized!.Filters);
        Assert.Equal("Year", filter.Column);
    }

    [Fact]
    public void Validate_NoDataset_Fails()
    {
        var data = new DataService(new NullDatasetPersistenceService());
        var request = new ChartSpecRequest { XColumn = "Region", Aggregation = Aggregation.Count };

        var result = ChartSpecValidator.Validate(request, data);

        Assert.False(result.IsValid);
    }
}
