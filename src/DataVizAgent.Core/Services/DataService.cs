using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using DataVizAgent.Models;
using DuckDB.NET.Data;

namespace DataVizAgent.Services;

/// <summary>
/// Dataset engine backed by an embedded DuckDB instance, so data is queried where it lives
/// instead of being materialized into .NET memory:
///
///   - CSV/TSV/Parquet files opened by path become a VIEW over the file (zero-copy — DuckDB's
///     sniffer handles delimiters, quoting, and headerless files, and queries stream from disk).
///   - XLSX / JSON / database imports / legacy session snapshots are parsed by the existing
///     readers and written into a DuckDB table (columnar, still far cheaper than row dictionaries).
///
/// All schema inference, profiling, filtering, and aggregation compile to SQL. The tolerant
/// numeric semantics are preserved: currency/percent-formatted strings parse as numbers, other
/// non-numeric cells are skipped (never coerced to 0) and counted for transparency.
/// </summary>
public sealed class DataService : IDataService, IDisposable
{
    private const string Relation = "dataset";
    private const int SchemaSampleRows = 20;
    private const int ProfileSampleValues = 8;
    private const double NumericColumnThreshold = 0.5;

    /// <summary>
    /// Wide datasets get a capped per-turn context: only this many columns are profiled and shown
    /// in sample rows (the full column NAME list is always sent — the agent needs exact names).
    /// Every prompt is re-processed from scratch each turn, so on CPU this cap is what keeps
    /// time-to-first-token sane for 50+ column files.
    /// </summary>
    private const int ProfiledColumnLimit = 24;

    /// <summary>Materialized datasets larger than this are not embedded into session/autosave files.</summary>
    private const int MaxEmbeddedSnapshotRows = 100_000;

    /// <summary>
    /// Identifier-column heuristic: below this many non-null values the "all distinct" signal is
    /// meaningless (a 10-row price list is legitimately all-distinct), so nothing is flagged.
    /// </summary>
    private const int IdentifierMinRows = 500;

    /// <summary>Fraction of non-null values that must be distinct for a column to count as an identifier.</summary>
    private const double IdentifierDistinctRatio = 0.95;

    /// <summary>
    /// SQL expression turning a VARCHAR cell into a number the way the old parser did: tolerate a
    /// currency-symbol prefix (after an optional sign), a percent suffix, and thousands separators;
    /// anything else (free text, blanks) becomes NULL rather than 0.
    /// </summary>
    private const string CleanNumericTemplate =
        "TRY_CAST(REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(TRIM({0}), '^(-?)\\s*[$€£¥]\\s*', '\\1'), '%$', ''), ',', '') AS DOUBLE)";

    private readonly IDatasetPersistenceService _datasetPersistenceService;
    private readonly DuckDBConnection _connection;
    private readonly object _dbLock = new();

    private List<ColumnInfo> _schema = [];
    private long _rowCount;
    private bool _hasDataset;
    private string _cachedProfile = string.Empty;
    private readonly Dictionary<string, bool> _identifierColumns = new(StringComparer.OrdinalIgnoreCase);
    private bool _isViewBacked;
    private int _persistVersion;

    public string? DatasetName { get; private set; }

    /// <summary>Path of the source file when the dataset is a view over it (queried in place).</summary>
    public string? SourcePath { get; private set; }

    public int RowCount => (int)Math.Clamp(_rowCount, 0, int.MaxValue);

    public event Action? Changed;

    public DataService(IDatasetPersistenceService datasetPersistenceService)
    {
        _datasetPersistenceService = datasetPersistenceService ?? throw new ArgumentNullException(nameof(datasetPersistenceService));
        _connection = new DuckDBConnection("DataSource=:memory:");
        _connection.Open();

        // The restored data came from the autosave file, so re-persisting it would be a no-op write.
        RestorePersistedDataset(_datasetPersistenceService.TryLoadDataset(), persist: false);
    }

    // ── Loading ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches a data file where it lives. CSV/TSV/TXT/Parquet become a zero-copy view over the
    /// file; XLSX and JSON are parsed and materialized into a table.
    /// </summary>
    public async Task LoadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected data file was not found.", path);

        string extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".csv" or ".tsv" or ".txt":
                // An XLSX saved with a .csv extension is really a ZIP — detect and reroute.
                await using (FileStream probe = File.OpenRead(path))
                {
                    if (LooksLikeZipArchive(probe))
                    {
                        await LoadSpreadsheetFromStreamAsync(probe, path, cancellationToken);
                        return;
                    }
                }

                AttachFileView(path, $"read_csv_auto({QuoteLiteral(path)})");
                break;

            case ".parquet":
                AttachFileView(path, $"read_parquet({QuoteLiteral(path)})");
                break;

            case ".xlsx":
                await using (FileStream stream = File.OpenRead(path))
                    await LoadSpreadsheetFromStreamAsync(stream, path, cancellationToken);
                return;

            case ".json" or ".ndjson":
                await using (FileStream stream = File.OpenRead(path))
                    await LoadJsonFromStreamAsync(stream, path, cancellationToken);
                return;

            default:
                throw new InvalidDataException($"Unsupported file type \"{extension}\". Use CSV, TSV, Parquet, XLSX, or JSON.");
        }

        CommitDataset(path, isViewBacked: true);
    }

    public Task LoadCsvAsync(string path, CancellationToken cancellationToken = default) =>
        LoadFileAsync(path, cancellationToken);

    public Task LoadJsonAsync(string path, CancellationToken cancellationToken = default) =>
        LoadFileAsync(path, cancellationToken);

    public async Task LoadCsvFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default)
    {
        if (LooksLikeZipArchive(stream))
        {
            await LoadSpreadsheetFromStreamAsync(stream, name, cancellationToken);
            return;
        }

        // Streams have no stable path to view over — copy to a temp file, let DuckDB's sniffer
        // parse it, and materialize into a table so the temp file can be deleted immediately.
        string tempPath = Path.Combine(Path.GetTempPath(), $"dva-upload-{Guid.NewGuid():N}.csv");
        try
        {
            await using (FileStream tempStream = File.Create(tempPath))
            {
                stream.Position = 0;
                await stream.CopyToAsync(tempStream, cancellationToken);
            }

            try
            {
                lock (_dbLock)
                {
                    DropRelationLocked();
                    using DbCommand command = _connection.CreateCommand();
                    command.CommandText = $"CREATE TABLE {Relation} AS SELECT * FROM read_csv_auto({QuoteLiteral(tempPath)})";
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException(
                    "The selected CSV file could not be read as plain text. If it came from Excel, upload the workbook or export it as CSV UTF-8.", ex);
            }

            CommitDataset(name, isViewBacked: false);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort temp cleanup */ }
        }
    }

    public async Task LoadSpreadsheetFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default)
    {
        List<string[]> sheetRows = await ReadSpreadsheetRowsAsync(stream, cancellationToken);
        if (sheetRows.Count == 0)
            throw new InvalidDataException("The spreadsheet is empty.");

        (string[] headers, List<Dictionary<string, object?>> rows) = LooksLikeEmbeddedCsvSheet(sheetRows)
            ? await ParseEmbeddedCsvRowsAsync(sheetRows, cancellationToken)
            : ParseTabularRows(sheetRows);

        MaterializeRows(name, headers, rows, persist: true);
    }

    public async Task LoadJsonFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default)
    {
        var rows = new List<Dictionary<string, object?>>();

        if (name.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (obj is not null) rows.Add(FlattenJsonObject(obj));
            }
        }
        else
        {
            var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object)
                    {
                        var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(element.GetRawText());
                        if (obj is not null) rows.Add(FlattenJsonObject(obj));
                    }
                }
            }
        }

        string[] headers = rows.Count > 0 ? [.. rows[0].Keys] : [];
        MaterializeRows(name, headers, rows, persist: true);
    }

    public void LoadRows(string name, string[] headers, List<Dictionary<string, object?>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        MaterializeRows(name, headers, rows, persist: true);
    }

    public void Clear()
    {
        lock (_dbLock)
        {
            DropRelationLocked();
        }

        DatasetName = null;
        SourcePath = null;
        _schema = [];
        _rowCount = 0;
        _hasDataset = false;
        _isViewBacked = false;
        _cachedProfile = string.Empty;
        _identifierColumns.Clear();
        NotifyChanged();
    }

    // ── Snapshots / persistence ────────────────────────────────────────────────

    public PersistedDatasetSnapshot? CreateSnapshot()
    {
        if (!_hasDataset || string.IsNullOrWhiteSpace(DatasetName))
            return null;

        string[] headers = [.. _schema.Select(column => column.Name)];

        // View-backed datasets persist as a reference to the file, not a copy of it.
        if (_isViewBacked && SourcePath is not null)
        {
            return new PersistedDatasetSnapshot
            {
                DatasetName = DatasetName,
                SourcePath = SourcePath,
                Headers = headers,
            };
        }

        if (_rowCount > MaxEmbeddedSnapshotRows)
            return null; // too large to embed; the report autosave still works without it

        var rows = new List<Dictionary<string, string?>>((int)_rowCount);
        lock (_dbLock)
        {
            using DbCommand command = _connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {Relation}";
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, string?>(headers.Length, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount && i < headers.Length; i++)
                    row[headers[i]] = reader.IsDBNull(i) ? null : FormatCell(reader.GetValue(i));
                rows.Add(row);
            }
        }

        return new PersistedDatasetSnapshot
        {
            DatasetName = DatasetName,
            Headers = headers,
            Rows = rows,
        };
    }

    public void LoadSnapshot(PersistedDatasetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RestorePersistedDataset(snapshot, persist: true);
    }

    private void RestorePersistedDataset(PersistedDatasetSnapshot? snapshot, bool persist)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.DatasetName))
            return;

        // Path-reference snapshot: re-attach the file in place when it still exists.
        if (!string.IsNullOrWhiteSpace(snapshot.SourcePath))
        {
            if (File.Exists(snapshot.SourcePath))
            {
                try
                {
                    LoadFileAsync(snapshot.SourcePath).GetAwaiter().GetResult();
                }
                catch
                {
                    // A moved/corrupt file shouldn't block startup; the restore hint guides the user.
                }
            }

            return;
        }

        if (snapshot.Headers.Length == 0)
            return;

        var rows = snapshot.Rows
            .Select(row => row.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase))
            .ToList();

        MaterializeRows(snapshot.DatasetName, snapshot.Headers, rows, persist);
    }

    // ── Schema & profiling ─────────────────────────────────────────────────────

    public IReadOnlyList<ColumnInfo> GetSchema() => _schema;

    public string GetDataSummary()
    {
        if (!_hasDataset) return "(no dataset loaded)";

        bool wide = _schema.Count > ProfiledColumnLimit;
        int sampleRows = wide ? 2 : 5;

        var sb = new StringBuilder();
        sb.Append($"Dataset: {DatasetName}, {_rowCount} rows. Columns: ");
        sb.Append(string.Join(", ", _schema.Select(c => $"{c.Name}:{c.Type.ToString().ToLowerInvariant()}")));
        sb.AppendLine();
        sb.AppendLine($"Sample rows (up to {sampleRows}{(wide ? $", first {ProfiledColumnLimit} columns" : "")}):");

        lock (_dbLock)
        {
            using DbCommand command = _connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {Relation} LIMIT {sampleRows}";
            using DbDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                int shown = Math.Min(reader.FieldCount, ProfiledColumnLimit);
                var cells = new List<string>(shown);
                for (int i = 0; i < shown; i++)
                    cells.Add($"{reader.GetName(i)}={(reader.IsDBNull(i) ? "" : Truncate(FormatCell(reader.GetValue(i)), 60))}");
                string more = reader.FieldCount > shown ? $" | …(+{reader.FieldCount - shown} more columns)" : string.Empty;
                sb.AppendLine("  " + string.Join(" | ", cells) + more);
            }
        }

        return sb.ToString().TrimEnd();
    }

    public string GetColumnProfile() => _hasDataset ? _cachedProfile : "(no dataset loaded)";

    /// <summary>
    /// Builds the per-column profile once per dataset commit. Cost matters here: all per-column
    /// statistics are computed in a SINGLE table scan (one wide aggregate query) plus one
    /// LIMIT-bounded read for sample values — per-column queries would re-parse a file-backed
    /// view once per column (observed: ~60 s for a 58-column CSV).
    /// </summary>
    private string BuildColumnProfile()
    {
        // Only the first ProfiledColumnLimit columns are profiled — the full name list is in the
        // data summary, and profiling every column of a wide file bloats every turn's prompt.
        List<ColumnInfo> profiled = [.. _schema.Take(ProfiledColumnLimit)];

        // One wide aggregate: min/max/avg plus identifier-detection stats (distinct count,
        // fractional-value count, non-null count) for Number columns, approximate distinct
        // count for everything else.
        var selects = new List<string>(profiled.Count * 6);
        foreach (ColumnInfo column in profiled)
        {
            string varcharExpr = $"CAST({QuoteIdentifier(column.Name)} AS VARCHAR)";
            string clean = string.Format(CultureInfo.InvariantCulture, CleanNumericTemplate, varcharExpr);
            if (column.Type == ColumnType.Number)
                selects.Add($"MIN({clean}), MAX({clean}), AVG({clean}), " +
                            $"approx_count_distinct({clean}), " +
                            $"COUNT(*) FILTER (WHERE {clean} IS NOT NULL AND {clean} <> FLOOR({clean})), " +
                            $"COUNT({clean})");
            else
                selects.Add($"approx_count_distinct(LOWER({varcharExpr})) FILTER (WHERE {varcharExpr} IS NOT NULL AND TRIM({varcharExpr}) <> '')");
        }

        var stats = new List<object?>();
        using (DbCommand command = _connection.CreateCommand())
        {
            command.CommandText = $"SELECT {string.Join(", ", selects)} FROM {Relation}";
            using DbDataReader reader = command.ExecuteReader();
            if (reader.Read())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                    stats.Add(reader.IsDBNull(i) ? null : reader.GetValue(i));
            }
        }

        // Sample values for non-numeric columns come from the first rows (bounded read).
        Dictionary<string, List<string>> samples = CollectSampleValues();

        var sb = new StringBuilder();
        sb.AppendLine("Column profiles (use these to pick good X/Y columns):");

        int cursor = 0;
        foreach (ColumnInfo column in profiled)
        {
            if (column.Type == ColumnType.Number)
            {
                object? min = stats.ElementAtOrDefault(cursor);
                object? max = stats.ElementAtOrDefault(cursor + 1);
                object? avg = stats.ElementAtOrDefault(cursor + 2);
                long distinct = Convert.ToInt64(stats.ElementAtOrDefault(cursor + 3) ?? 0L, CultureInfo.InvariantCulture);
                long fractional = Convert.ToInt64(stats.ElementAtOrDefault(cursor + 4) ?? 0L, CultureInfo.InvariantCulture);
                long nonNull = Convert.ToInt64(stats.ElementAtOrDefault(cursor + 5) ?? 0L, CultureInfo.InvariantCulture);
                cursor += 6;

                if (min is null)
                {
                    sb.AppendLine($"  {column.Name} (number): no numeric values");
                }
                else
                {
                    bool isIdentifier = IsIdentifierLike(distinct, fractional, nonNull);
                    _identifierColumns[column.Name] = isIdentifier;

                    double minValue = Convert.ToDouble(min, CultureInfo.InvariantCulture);
                    double maxValue = Convert.ToDouble(max, CultureInfo.InvariantCulture);
                    double avgValue = Convert.ToDouble(avg, CultureInfo.InvariantCulture);
                    string idNote = isIdentifier
                        ? " — unique ID per row; use count to count rows, never sum or average this column"
                        : string.Empty;
                    sb.AppendLine(FormatInvariant($"  {column.Name} (number): range {minValue:0.##} to {maxValue:0.##}, avg {avgValue:0.##}{idNote}"));
                }
            }
            else
            {
                long distinct = Convert.ToInt64(stats.ElementAtOrDefault(cursor) ?? 0L, CultureInfo.InvariantCulture);
                cursor += 1;

                List<string> values = samples.GetValueOrDefault(column.Name) ?? [];
                string typeLabel = column.Type.ToString().ToLowerInvariant();
                string sampleText = string.Join(", ", values.Select(v => $"\"{v}\""));
                string suffix = distinct > values.Count ? ", …" : string.Empty;
                sb.AppendLine($"  {column.Name} ({typeLabel}): {distinct} distinct values — {sampleText}{suffix}");
            }
        }

        if (_schema.Count > profiled.Count)
            sb.AppendLine($"  (+{_schema.Count - profiled.Count} more columns not profiled — their names are in the dataset summary; query them directly.)");

        return sb.ToString().TrimEnd();
    }

    /// <summary>Up to 8 distinct sample values per profiled column, drawn from the first 200 rows (one bounded read).</summary>
    private Dictionary<string, List<string>> CollectSampleValues()
    {
        var samples = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (ColumnInfo column in _schema.Take(ProfiledColumnLimit))
        {
            samples[column.Name] = [];
            seen[column.Name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        using DbCommand command = _connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Relation} LIMIT 200";
        using DbDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount && i < _schema.Count; i++)
            {
                string name = reader.GetName(i);
                if (!samples.TryGetValue(name, out List<string>? list) || list.Count >= ProfileSampleValues || reader.IsDBNull(i))
                    continue;

                string value = Truncate(FormatCell(reader.GetValue(i)), 40);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (seen[name].Add(value))
                    list.Add(value);
            }
        }

        return samples;
    }

    /// <inheritdoc />
    public bool IsLikelyIdentifierColumn(string columnName)
    {
        if (!_hasDataset || string.IsNullOrWhiteSpace(columnName))
            return false;

        lock (_dbLock)
        {
            if (_identifierColumns.TryGetValue(columnName, out bool cached))
                return cached;

            // Profiled columns were measured during the profile scan; anything else (a column
            // beyond the profile limit) is measured on demand with one single-column scan.
            ColumnInfo? column = _schema.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
            if (column is null || column.Type != ColumnType.Number)
                return false;

            string varcharExpr = $"CAST({QuoteIdentifier(column.Name)} AS VARCHAR)";
            string clean = string.Format(CultureInfo.InvariantCulture, CleanNumericTemplate, varcharExpr);

            using DbCommand command = _connection.CreateCommand();
            command.CommandText =
                $"SELECT approx_count_distinct({clean}), " +
                $"COUNT(*) FILTER (WHERE {clean} IS NOT NULL AND {clean} <> FLOOR({clean})), " +
                $"COUNT({clean}) FROM {Relation}";

            bool result = false;
            using (DbDataReader reader = command.ExecuteReader())
            {
                if (reader.Read())
                {
                    long distinct = reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
                    long fractional = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                    long nonNull = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
                    result = IsIdentifierLike(distinct, fractional, nonNull);
                }
            }

            _identifierColumns[column.Name] = result;
            return result;
        }
    }

    /// <summary>
    /// A column is identifier-like when it has enough rows for the signal to mean something,
    /// contains only whole numbers, and is distinct on (nearly) every row.
    /// </summary>
    private static bool IsIdentifierLike(long distinct, long fractionalCount, long nonNullCount) =>
        nonNullCount >= IdentifierMinRows &&
        fractionalCount == 0 &&
        distinct >= (long)(nonNullCount * IdentifierDistinctRatio);

    // ── Querying ───────────────────────────────────────────────────────────────

    public IReadOnlyList<(string Label, double Value)> QuerySeries(string xColumn, string yColumn, Aggregation aggregation) =>
        QuerySeries(xColumn, yColumn, aggregation, []);

    public IReadOnlyList<(string Label, double Value)> QuerySeries(string xColumn, string yColumn, Aggregation aggregation, IReadOnlyList<DataFilter> filters) =>
        QuerySeriesWithStats(xColumn, yColumn, aggregation, filters).Points;

    public SeriesResult QuerySeriesWithStats(string xColumn, string yColumn, Aggregation aggregation, IReadOnlyList<DataFilter> filters)
    {
        if (!_hasDataset)
            return new SeriesResult([], 0);

        var parameters = new List<object>();
        string where = BuildFilterClause(filters, parameters);

        string label = $"COALESCE(CAST({QuoteIdentifier(xColumn)} AS VARCHAR), '(null)')";
        string yVarchar = string.IsNullOrWhiteSpace(yColumn)
            ? "NULL"
            : $"CAST({QuoteIdentifier(yColumn)} AS VARCHAR)";
        string clean = string.Format(CultureInfo.InvariantCulture, CleanNumericTemplate, yVarchar);

        string valueExpression = aggregation switch
        {
            Aggregation.Count => "COUNT(*)",
            Aggregation.Sum => $"COALESCE(SUM({clean}), 0)",
            Aggregation.Average => $"COALESCE(AVG({clean}), 0)",
            Aggregation.Min => $"COALESCE(MIN({clean}), 0)",
            Aggregation.Max => $"COALESCE(MAX({clean}), 0)",
            _ => $"COALESCE(FIRST({clean}) FILTER (WHERE {clean} IS NOT NULL), 0)",
        };

        var points = new List<(string, double)>();
        int ignored = 0;

        lock (_dbLock)
        {
            using (DbCommand command = _connection.CreateCommand())
            {
                command.CommandText =
                    $"SELECT {label} AS label, CAST({valueExpression} AS DOUBLE) AS value " +
                    $"FROM {Relation}{where} GROUP BY label ORDER BY label";
                AddParameters(command, parameters);

                using DbDataReader reader = command.ExecuteReader();
                while (reader.Read())
                    points.Add((reader.GetString(0), reader.IsDBNull(1) ? 0d : reader.GetDouble(1)));
            }

            // Transparency: count non-empty Y cells that were not usable as numbers.
            if (aggregation != Aggregation.Count && !string.IsNullOrWhiteSpace(yColumn))
            {
                using DbCommand command = _connection.CreateCommand();
                command.CommandText =
                    $"SELECT COUNT(*) FROM {Relation}{where.Replace("WHERE", "WHERE", StringComparison.Ordinal)}" +
                    (where.Length == 0 ? " WHERE " : " AND ") +
                    $"{yVarchar} IS NOT NULL AND TRIM({yVarchar}) <> '' AND {clean} IS NULL";
                AddParameters(command, parameters);
                ignored = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        return new SeriesResult(points, ignored);
    }

    /// <summary>Translates <see cref="DataFilter"/>s to a parameterized WHERE clause, matching the legacy semantics.</summary>
    private static string BuildFilterClause(IReadOnlyList<DataFilter> filters, List<object> parameters)
    {
        if (filters is not { Count: > 0 })
            return string.Empty;

        var clauses = new List<string>();
        foreach (DataFilter filter in filters)
        {
            string basis = $"COALESCE(CAST({QuoteIdentifier(filter.Column)} AS VARCHAR), '')";
            switch (filter.Operator)
            {
                case FilterOperator.Equals:
                    clauses.Add($"LOWER({basis}) = LOWER(?)");
                    parameters.Add(filter.Value);
                    break;
                case FilterOperator.NotEquals:
                    clauses.Add($"LOWER({basis}) <> LOWER(?)");
                    parameters.Add(filter.Value);
                    break;
                case FilterOperator.Contains:
                    clauses.Add($"POSITION(LOWER(?) IN LOWER({basis})) > 0");
                    parameters.Add(filter.Value);
                    break;
                default:
                    string op = filter.Operator switch
                    {
                        FilterOperator.GreaterThan => ">",
                        FilterOperator.GreaterThanOrEqual => ">=",
                        FilterOperator.LessThan => "<",
                        _ => "<=",
                    };
                    // Numeric comparison when both sides parse as numbers, else case-insensitive text.
                    clauses.Add(
                        $"(CASE WHEN TRY_CAST({basis} AS DOUBLE) IS NOT NULL AND TRY_CAST(? AS DOUBLE) IS NOT NULL " +
                        $"THEN TRY_CAST({basis} AS DOUBLE) {op} TRY_CAST(? AS DOUBLE) " +
                        $"ELSE LOWER({basis}) {op} LOWER(?) END)");
                    parameters.Add(filter.Value);
                    parameters.Add(filter.Value);
                    parameters.Add(filter.Value);
                    break;
            }
        }

        return " WHERE " + string.Join(" AND ", clauses);
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drops the current dataset relation whatever its type. DuckDB's <c>DROP VIEW IF EXISTS</c>
    /// raises a catalog error when the name exists as a TABLE (and vice versa), so the type is
    /// looked up first. Callers must hold <see cref="_dbLock"/>.
    /// </summary>
    private void DropRelationLocked()
    {
        using DbCommand probe = _connection.CreateCommand();
        probe.CommandText = $"SELECT table_type FROM information_schema.tables WHERE table_name = '{Relation}'";
        object? type = probe.ExecuteScalar();
        if (type is null or DBNull)
            return;

        using DbCommand drop = _connection.CreateCommand();
        drop.CommandText = string.Equals(Convert.ToString(type), "VIEW", StringComparison.OrdinalIgnoreCase)
            ? $"DROP VIEW {Relation}"
            : $"DROP TABLE {Relation}";
        drop.ExecuteNonQuery();
    }

    private void AttachFileView(string path, string readerFunction)
    {
        lock (_dbLock)
        {
            DropRelationLocked();
            using DbCommand command = _connection.CreateCommand();
            command.CommandText = $"CREATE VIEW {Relation} AS SELECT * FROM {readerFunction}";
            try
            {
                command.ExecuteNonQuery();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException($"DuckDB could not read \"{Path.GetFileName(path)}\": {ex.Message}", ex);
            }
        }

        SourcePath = path;
    }

    /// <summary>Writes parsed rows into a DuckDB table (all VARCHAR, preserving legacy string semantics).</summary>
    private void MaterializeRows(string name, string[] headers, List<Dictionary<string, object?>> rows, bool persist)
    {
        string[] normalized = DedupeHeaders(NormalizeHeaders(headers.Length > 0 ? headers : rows.FirstOrDefault()?.Keys.ToArray() ?? []));

        lock (_dbLock)
        {
            DropRelationLocked();
            string columnList = string.Join(", ", normalized.Select(h => $"{QuoteIdentifier(h)} VARCHAR"));
            using (DbCommand create = _connection.CreateCommand())
            {
                create.CommandText = $"CREATE TABLE {Relation} ({columnList})";
                create.ExecuteNonQuery();
            }

            // Batched literal inserts: simple, transactional, and fast enough for materialized sizes.
            const int batchSize = 500;
            var batch = new StringBuilder();
            using DbTransaction transaction = _connection.BeginTransaction();
            for (int offset = 0; offset < rows.Count; offset += batchSize)
            {
                batch.Clear();
                batch.Append($"INSERT INTO {Relation} VALUES ");
                int end = Math.Min(offset + batchSize, rows.Count);
                for (int r = offset; r < end; r++)
                {
                    if (r > offset) batch.Append(", ");
                    batch.Append('(');
                    for (int c = 0; c < normalized.Length; c++)
                    {
                        if (c > 0) batch.Append(", ");
                        // Row keys use the ORIGINAL header names; normalized names only rename duplicates/blanks.
                        string key = c < headers.Length ? headers[c] : normalized[c];
                        object? value = rows[r].GetValueOrDefault(key);
                        string? text = value is null ? null : FormatCell(value);
                        batch.Append(text is null ? "NULL" : QuoteLiteral(text));
                    }
                    batch.Append(')');
                }

                using DbCommand insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = batch.ToString();
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        SourcePath = null;
        CommitDataset(name, isViewBacked: false, persist);
    }

    private void CommitDataset(string name, bool isViewBacked, bool persist = true)
    {
        DatasetName = Path.GetFileNameWithoutExtension(name);
        _isViewBacked = isViewBacked;
        if (!isViewBacked)
            SourcePath = null;
        _hasDataset = true;

        lock (_dbLock)
        {
            _rowCount = CountRows();
            _schema = InferSchema();
            _identifierColumns.Clear();
            _cachedProfile = BuildColumnProfile();
        }

        if (persist)
            PersistCurrentDatasetInBackground();

        NotifyChanged();
    }

    private long CountRows()
    {
        using DbCommand command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {Relation}";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private List<ColumnInfo> InferSchema()
    {
        var columns = new List<(string Name, string DuckType)>();
        using (DbCommand describe = _connection.CreateCommand())
        {
            describe.CommandText = $"DESCRIBE {Relation}";
            using DbDataReader reader = describe.ExecuteReader();
            while (reader.Read())
                columns.Add((reader.GetString(0), reader.GetString(1)));
        }

        // Text columns get the legacy tolerant classification (numeric when ≥ half the sampled
        // non-empty values parse, date when all do). All columns are classified in ONE query over
        // one LIMIT-ed sample — per-column queries would re-sniff a file-backed view every time.
        var textColumns = new List<string>();
        foreach ((string columnName, string duckType) in columns)
        {
            if (!IsNumericDuckType(duckType) && !IsDateDuckType(duckType))
                textColumns.Add(columnName);
        }

        Dictionary<string, ColumnType> textTypes = ClassifyTextColumns(textColumns);

        var schema = new List<ColumnInfo>(columns.Count);
        foreach ((string columnName, string duckType) in columns)
        {
            ColumnType type = IsNumericDuckType(duckType) ? ColumnType.Number
                : IsDateDuckType(duckType) ? ColumnType.Date
                : textTypes.GetValueOrDefault(columnName, ColumnType.String);
            schema.Add(new ColumnInfo(columnName, type));
        }

        return schema;
    }

    private static bool IsNumericDuckType(string duckType)
    {
        string upper = duckType.ToUpperInvariant();
        return upper.Contains("INT") || upper.Contains("DOUBLE") || upper.Contains("FLOAT") || upper.Contains("DECIMAL") || upper.Contains("NUMERIC");
    }

    private static bool IsDateDuckType(string duckType)
    {
        string upper = duckType.ToUpperInvariant();
        return upper.Contains("DATE") || upper.Contains("TIMESTAMP");
    }

    private Dictionary<string, ColumnType> ClassifyTextColumns(List<string> columnNames)
    {
        var result = new Dictionary<string, ColumnType>(StringComparer.OrdinalIgnoreCase);
        if (columnNames.Count == 0)
            return result;

        // Three aggregates per column over one shared LIMIT-ed sample of the first rows.
        var selects = new List<string>(columnNames.Count * 3);
        var inner = new List<string>(columnNames.Count);
        for (int i = 0; i < columnNames.Count; i++)
        {
            string alias = $"v{i}";
            inner.Add($"CAST({QuoteIdentifier(columnNames[i])} AS VARCHAR) AS {alias}");
            string nonEmpty = $"{alias} IS NOT NULL AND TRIM({alias}) <> ''";
            string clean = string.Format(CultureInfo.InvariantCulture, CleanNumericTemplate, alias);
            selects.Add($"COUNT(*) FILTER (WHERE {nonEmpty})");
            selects.Add($"COUNT(*) FILTER (WHERE {nonEmpty} AND {clean} IS NOT NULL)");
            selects.Add($"COUNT(*) FILTER (WHERE {nonEmpty} AND (TRY_CAST({alias} AS TIMESTAMP) IS NOT NULL OR TRY_CAST({alias} AS DATE) IS NOT NULL))");
        }

        using DbCommand command = _connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(", ", selects)} FROM (SELECT {string.Join(", ", inner)} FROM {Relation} LIMIT {SchemaSampleRows})";

        using DbDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            return result;

        for (int i = 0; i < columnNames.Count; i++)
        {
            long total = reader.GetInt64(i * 3);
            long numeric = reader.GetInt64(i * 3 + 1);
            long dates = reader.GetInt64(i * 3 + 2);

            ColumnType type = total == 0 ? ColumnType.String
                : numeric > 0 && numeric >= total * NumericColumnThreshold ? ColumnType.Number
                : dates == total ? ColumnType.Date
                : ColumnType.String;
            result[columnNames[i]] = type;
        }

        return result;
    }

    private void PersistCurrentDatasetInBackground()
    {
        int version = Interlocked.Increment(ref _persistVersion);
        _ = Task.Run(() =>
        {
            try
            {
                if (Volatile.Read(ref _persistVersion) != version)
                    return;

                PersistedDatasetSnapshot? snapshot = CreateSnapshot();
                if (snapshot is null || Volatile.Read(ref _persistVersion) != version)
                    return;

                _datasetPersistenceService.SaveDataset(snapshot);
            }
            catch
            {
                // Autosave is best-effort.
            }
        });
    }

    private static void AddParameters(DbCommand command, List<object> parameters)
    {
        foreach (object value in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static string QuoteLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private static string FormatInvariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>Caps cell text sent to the model (sample rows/values) so URL-heavy datasets don't bloat the prompt.</summary>
    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    private static string FormatCell(object value) => value switch
    {
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        bool booleanValue => booleanValue ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private void NotifyChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _connection.Dispose();
    }

    // ── Legacy parsers (XLSX + embedded CSV sheets + JSON flattening) ─────────

    private static Dictionary<string, object?> FlattenJsonObject(Dictionary<string, JsonElement> obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in obj)
        {
            dict[kv.Key] = kv.Value.ValueKind switch
            {
                JsonValueKind.Number => kv.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => kv.Value.GetString()
            };
        }
        return dict;
    }

    private static readonly XNamespace SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static async Task<List<string[]>> ReadSpreadsheetRowsAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var workbookStream = new MemoryStream();
        stream.Position = 0;
        await stream.CopyToAsync(workbookStream, cancellationToken);
        workbookStream.Position = 0;

        using var archive = new ZipArchive(workbookStream, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry? worksheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? archive.Entries.FirstOrDefault(entry => entry.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

        if (worksheetEntry is null)
            throw new InvalidDataException("The spreadsheet does not contain a readable worksheet.");

        IReadOnlyList<string> sharedStrings = ReadSharedStrings(archive);
        using Stream worksheetStream = worksheetEntry.Open();
        XDocument worksheet = await XDocument.LoadAsync(worksheetStream, LoadOptions.None, cancellationToken);

        return worksheet.Root?
            .Element(SpreadsheetNamespace + "sheetData")?
            .Elements(SpreadsheetNamespace + "row")
            .Select(row => ReadWorksheetRow(row, sharedStrings))
            .Where(row => row.Length > 0)
            .ToList()
            ?? [];
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        ZipArchiveEntry? sharedStringEntry = archive.GetEntry("xl/sharedStrings.xml");
        if (sharedStringEntry is null)
            return [];

        using Stream sharedStringStream = sharedStringEntry.Open();
        XDocument sharedStrings = XDocument.Load(sharedStringStream);

        return sharedStrings.Root?
            .Elements(SpreadsheetNamespace + "si")
            .Select(item => string.Concat(item.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value)))
            .ToArray()
            ?? [];
    }

    private static string[] ReadWorksheetRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var values = new List<string>();
        int currentColumn = 1;

        foreach (XElement cell in row.Elements(SpreadsheetNamespace + "c"))
        {
            int targetColumn = GetColumnIndex((string?)cell.Attribute("r"));
            while (currentColumn < targetColumn)
            {
                values.Add(string.Empty);
                currentColumn++;
            }

            values.Add(ReadWorksheetCell(cell, sharedStrings));
            currentColumn++;
        }

        while (values.Count > 0 && string.IsNullOrWhiteSpace(values[^1]))
            values.RemoveAt(values.Count - 1);

        return [.. values];
    }

    private static string ReadWorksheetCell(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        string? cellType = (string?)cell.Attribute("t");
        if (string.Equals(cellType, "s", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(cell.Element(SpreadsheetNamespace + "v")?.Value, out int sharedStringIndex)
            && sharedStringIndex >= 0
            && sharedStringIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedStringIndex];
        }

        if (string.Equals(cellType, "inlineStr", StringComparison.OrdinalIgnoreCase))
            return string.Concat(cell.Descendants(SpreadsheetNamespace + "t").Select(text => text.Value));

        return cell.Element(SpreadsheetNamespace + "v")?.Value ?? string.Empty;
    }

    private static int GetColumnIndex(string? cellReference)
    {
        if (string.IsNullOrWhiteSpace(cellReference))
            return 1;

        int columnIndex = 0;
        foreach (char ch in cellReference)
        {
            if (!char.IsLetter(ch))
                break;

            columnIndex = (columnIndex * 26) + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return columnIndex == 0 ? 1 : columnIndex;
    }

    private static bool LooksLikeEmbeddedCsvSheet(List<string[]> sheetRows) =>
        sheetRows.Count > 0
        && sheetRows[0].Length == 1
        && sheetRows[0][0].Contains(',', StringComparison.Ordinal)
        && sheetRows.All(row => row.Length <= 1);

    private static async Task<(string[] Headers, List<Dictionary<string, object?>> Rows)> ParseEmbeddedCsvRowsAsync(List<string[]> sheetRows, CancellationToken cancellationToken)
    {
        string csvText = string.Join(Environment.NewLine, sheetRows.Select(row => row.Length == 0 ? string.Empty : row[0]));
        using var reader = new StringReader(csvText);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
        });

        if (!await csv.ReadAsync())
            throw new InvalidDataException("The CSV file is empty.");

        csv.ReadHeader();
        string[] headers = NormalizeHeaders(csv.HeaderRecord ?? []);
        var rows = new List<Dictionary<string, object?>>();

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < headers.Length; index++)
                row[headers[index]] = csv.GetField(index);

            rows.Add(row);
        }

        return (headers, rows);
    }

    private static (string[] Headers, List<Dictionary<string, object?>> Rows) ParseTabularRows(List<string[]> sheetRows)
    {
        string[] headers = NormalizeHeaders(sheetRows[0]);
        var rows = new List<Dictionary<string, object?>>();

        foreach (string[] sheetRow in sheetRows.Skip(1))
        {
            if (sheetRow.All(string.IsNullOrWhiteSpace))
                continue;

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < headers.Length; index++)
                row[headers[index]] = index < sheetRow.Length ? sheetRow[index] : null;

            rows.Add(row);
        }

        return (headers, rows);
    }

    private static string[] NormalizeHeaders(IEnumerable<string> rawHeaders)
    {
        string[] headers = rawHeaders
            .Select((header, index) => string.IsNullOrWhiteSpace(header) ? $"Column {index + 1}" : header.Trim())
            .ToArray();

        if (headers.Length == 0)
            throw new InvalidDataException("The dataset does not contain a header row.");

        return headers;
    }

    /// <summary>SQL tables reject duplicate column names, so repeats get a numeric suffix.</summary>
    private static string[] DedupeHeaders(string[] headers)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new string[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            string name = headers[i];
            if (seen.TryGetValue(name, out int count))
            {
                seen[name] = count + 1;
                result[i] = $"{name}_{count + 1}";
            }
            else
            {
                seen[name] = 1;
                result[i] = name;
            }
        }

        return result;
    }

    private static bool LooksLikeZipArchive(Stream stream)
    {
        long originalPosition = stream.Position;
        Span<byte> header = stackalloc byte[4];
        stream.Position = 0;
        int bytesRead = stream.Read(header);
        stream.Position = originalPosition;

        return bytesRead == 4
            && header[0] == (byte)'P'
            && header[1] == (byte)'K'
            && header[2] == 0x03
            && header[3] == 0x04;
    }
}
