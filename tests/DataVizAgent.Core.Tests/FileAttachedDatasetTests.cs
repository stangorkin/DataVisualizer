using DataVizAgent.Models;
using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

/// <summary>Covers the query-in-place path: files attached as DuckDB views rather than loaded into memory.</summary>
public class FileAttachedDatasetTests
{
    private static string WriteTempFile(string name, string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dva-test-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task LoadFileAsync_HeaderlessTabDelimited_SniffsDelimiterAndColumns()
    {
        // GDELT-style: tab-delimited, no header row, mixed types.
        string path = WriteTempFile("gdelt.csv",
            "1001\t20260711\tZWE\tLondon\t-5.0\n" +
            "1002\t20260711\tUSA\tParis\t3.2\n" +
            "1003\t20260712\tZWE\tBerlin\t1.1\n");
        try
        {
            var data = new DataService(new NullDatasetPersistenceService());
            await data.LoadFileAsync(path);

            Assert.Equal(3, data.RowCount);
            Assert.Equal(5, data.GetSchema().Count);           // tabs sniffed, not commas
            Assert.Equal(path, data.SourcePath);               // queried in place
            Assert.Contains(data.GetSchema(), c => c.Type == ColumnType.Number);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadFileAsync_AggregatesDirectlyFromTheFile()
    {
        string path = WriteTempFile("sales.csv",
            "Region,Sales\nWest,100\nWest,150\nEast,80\n");
        try
        {
            var data = new DataService(new NullDatasetPersistenceService());
            await data.LoadFileAsync(path);

            var series = data.QuerySeries("Region", "Sales", Aggregation.Sum).ToDictionary(s => s.Label, s => s.Value);
            Assert.Equal(250, series["West"]);
            Assert.Equal(80, series["East"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CreateSnapshot_FileAttached_StoresPathReferenceNotRows()
    {
        string path = WriteTempFile("ref.csv", "A,B\n1,x\n2,y\n");
        try
        {
            var data = new DataService(new NullDatasetPersistenceService());
            await data.LoadFileAsync(path);

            PersistedDatasetSnapshot? snapshot = data.CreateSnapshot();

            Assert.NotNull(snapshot);
            Assert.Equal(path, snapshot!.SourcePath);
            Assert.Empty(snapshot.Rows);                       // no data copied into the snapshot
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LoadSnapshot_PathReference_ReattachesTheFile()
    {
        string path = WriteTempFile("reattach.csv", "Region,Sales\nWest,100\nEast,80\n");
        try
        {
            var source = new DataService(new NullDatasetPersistenceService());
            await source.LoadFileAsync(path);
            PersistedDatasetSnapshot snapshot = source.CreateSnapshot()!;

            var restored = new DataService(new NullDatasetPersistenceService());
            restored.LoadSnapshot(snapshot);

            Assert.Equal(2, restored.RowCount);
            Assert.Equal(path, restored.SourcePath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LoadSnapshot_PathReference_MissingFile_LeavesDatasetEmpty()
    {
        var data = new DataService(new NullDatasetPersistenceService());
        data.LoadSnapshot(new PersistedDatasetSnapshot
        {
            DatasetName = "gone",
            SourcePath = Path.Combine(Path.GetTempPath(), $"dva-missing-{Guid.NewGuid():N}.csv"),
            Headers = ["A"],
        });

        Assert.Equal(0, data.RowCount); // silently skipped; restore hint guides the user
    }
}
