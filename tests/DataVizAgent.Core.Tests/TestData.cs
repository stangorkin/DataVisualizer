using System.Text;
using DataVizAgent.Services;

namespace DataVizAgent.Core.Tests;

/// <summary>Dataset persistence stub so <see cref="DataService"/> can be exercised without touching disk.</summary>
internal sealed class NullDatasetPersistenceService : IDatasetPersistenceService
{
    public PersistedDatasetSnapshot? TryLoadDataset() => null;
    public void SaveDataset(PersistedDatasetSnapshot snapshot) { }
    public void DeleteSavedDataset() { }
}

internal static class TestData
{
    /// <summary>Builds a real <see cref="DataService"/> loaded with a small sales dataset.</summary>
    public static DataService CreateSalesDataService()
    {
        const string csv =
            "Region,Year,Sales,Customer\n" +
            "West,2023,100,Acme\n" +
            "West,2024,150,Globex\n" +
            "East,2023,80,Initech\n" +
            "East,2024,120,Umbrella\n" +
            "North,2024,200,Stark\n";

        var service = new DataService(new NullDatasetPersistenceService());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        service.LoadCsvFromStreamAsync(stream, "sales.csv").GetAwaiter().GetResult();
        return service;
    }
}
