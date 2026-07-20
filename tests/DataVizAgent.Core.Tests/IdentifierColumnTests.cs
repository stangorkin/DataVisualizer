using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

/// <summary>
/// Covers identifier-column detection and the validation guard that stops sum/average over
/// per-row unique keys (the "15 trillion entries" failure: summing an event-ID column and
/// presenting the result as a count).
/// </summary>
public class IdentifierColumnTests
{
    private static DataService CreateService(int rows)
    {
        var data = new DataService(new NullDatasetPersistenceService());
        var list = new List<Dictionary<string, object?>>(rows);
        for (int i = 0; i < rows; i++)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["EventId"] = 1_000_000 + i,                 // unique integer key
                ["Region"] = i % 2 == 0 ? "West" : "East",   // low-cardinality category
                ["Sales"] = 10.5 + (i % 7),                  // repeating fractional measure
            });
        }

        data.LoadRows("ids", ["EventId", "Region", "Sales"], list);
        return data;
    }

    [Fact]
    public void IsLikelyIdentifierColumn_DetectsUniqueIntegerKey()
    {
        var data = CreateService(rows: 600);

        Assert.True(data.IsLikelyIdentifierColumn("EventId"));
        Assert.False(data.IsLikelyIdentifierColumn("Region")); // text column
        Assert.False(data.IsLikelyIdentifierColumn("Sales"));  // fractional values, repeats
    }

    [Fact]
    public void IsLikelyIdentifierColumn_SmallDataset_IsNeverFlagged()
    {
        // A 10-row table with all-distinct integers is normal data, not a key.
        var data = CreateService(rows: 10);

        Assert.False(data.IsLikelyIdentifierColumn("EventId"));
    }

    [Fact]
    public void Validate_SumOverIdentifier_IsRejectedWithCountSuggestion()
    {
        var data = CreateService(rows: 600);
        var request = new ChartSpecRequest { XColumn = "Region", YColumn = "EventId", Aggregation = Aggregation.Sum };

        ChartSpecValidationResult result = ChartSpecValidator.Validate(request, data);

        Assert.False(result.IsValid);
        Assert.Contains("identifier", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("count", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_CountAndLegitimateSum_StillAllowed()
    {
        var data = CreateService(rows: 600);

        var count = new ChartSpecRequest { XColumn = "Region", Aggregation = Aggregation.Count };
        Assert.True(ChartSpecValidator.Validate(count, data).IsValid);

        var sumSales = new ChartSpecRequest { XColumn = "Region", YColumn = "Sales", Aggregation = Aggregation.Sum };
        Assert.True(ChartSpecValidator.Validate(sumSales, data).IsValid);
    }

    [Fact]
    public void ColumnProfile_TagsIdentifierColumnsForTheModel()
    {
        var data = CreateService(rows: 600);

        string profile = data.GetColumnProfile();

        Assert.Contains("unique ID per row", profile);
        // Only the key column is tagged, not the measure.
        string salesLine = profile.Split('\n').Single(l => l.Contains("Sales (number)"));
        Assert.DoesNotContain("unique ID", salesLine);
    }
}
