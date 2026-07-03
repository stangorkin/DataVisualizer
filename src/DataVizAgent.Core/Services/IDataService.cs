using DataVizAgent.Models;

namespace DataVizAgent.Services;

public sealed record ColumnInfo(string Name, ColumnType Type);

public enum ColumnType { String, Number, Date }

public interface IDataService
{
    event Action? Changed;

    Task LoadCsvAsync(string path, CancellationToken cancellationToken = default);
    Task LoadJsonAsync(string path, CancellationToken cancellationToken = default);
    Task LoadCsvFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default);
    Task LoadSpreadsheetFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default);
    Task LoadJsonFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default);

    string? DatasetName { get; }
    int RowCount { get; }

    IReadOnlyList<ColumnInfo> GetSchema();
    string GetDataSummary();
    string GetColumnProfile();
    PersistedDatasetSnapshot? CreateSnapshot();
    void LoadSnapshot(PersistedDatasetSnapshot snapshot);
    void LoadRows(string name, string[] headers, List<Dictionary<string, object?>> rows);
    void Clear();

    IReadOnlyList<(string Label, double Value)> QuerySeries(string xColumn, string yColumn, Aggregation aggregation);
    IReadOnlyList<(string Label, double Value)> QuerySeries(string xColumn, string yColumn, Aggregation aggregation, IReadOnlyList<DataFilter> filters);

    /// <summary>
    /// Like <see cref="QuerySeries(string, string, Aggregation, IReadOnlyList{DataFilter})"/> but also
    /// reports how many non-empty Y values weren't numeric and were skipped — for transparency.
    /// </summary>
    SeriesResult QuerySeriesWithStats(string xColumn, string yColumn, Aggregation aggregation, IReadOnlyList<DataFilter> filters);
}

/// <summary>A computed series plus a count of non-empty values that couldn't be parsed as numbers.</summary>
public readonly record struct SeriesResult(IReadOnlyList<(string Label, double Value)> Points, int IgnoredNonNumericCount);

public enum Aggregation { None, Sum, Average, Count, Min, Max }