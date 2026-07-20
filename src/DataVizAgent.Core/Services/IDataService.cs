using DataVizAgent.Models;

namespace DataVizAgent.Services;

public sealed record ColumnInfo(string Name, ColumnType Type);

public enum ColumnType { String, Number, Date }

public interface IDataService
{
    event Action? Changed;

    /// <summary>
    /// Attaches a data file where it lives on disk (CSV/TSV/Parquet become a zero-copy view;
    /// XLSX/JSON are parsed and materialized). Preferred for large files — nothing is copied
    /// into application memory.
    /// </summary>
    Task LoadFileAsync(string path, CancellationToken cancellationToken = default);

    Task LoadCsvAsync(string path, CancellationToken cancellationToken = default);
    Task LoadJsonAsync(string path, CancellationToken cancellationToken = default);
    Task LoadCsvFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default);
    Task LoadSpreadsheetFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default);
    Task LoadJsonFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default);

    string? DatasetName { get; }

    /// <summary>Path of the source file when the dataset is queried in place, else null.</summary>
    string? SourcePath { get; }

    int RowCount { get; }

    IReadOnlyList<ColumnInfo> GetSchema();
    string GetDataSummary();
    string GetColumnProfile();

    /// <summary>
    /// True when the column looks like a per-row unique key (integer values, distinct on nearly
    /// every row). Summing or averaging such a column is essentially never meaningful — chart
    /// validation refuses it and steers toward count.
    /// </summary>
    bool IsLikelyIdentifierColumn(string columnName);
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