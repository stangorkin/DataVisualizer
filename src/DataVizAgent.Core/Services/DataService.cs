using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using DataVizAgent.Models;

namespace DataVizAgent.Services;
public sealed class DataService : IDataService
{
    private const string CurrencySymbols = "$€£¥";
    private static readonly XNamespace SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly IDatasetPersistenceService _datasetPersistenceService;

    private List<Dictionary<string, object?>> _rows = [];
    private List<ColumnInfo> _schema = [];
    private int _persistVersion;

    public string? DatasetName { get; private set; }
    public int RowCount => _rows.Count;
    public event Action? Changed;

    public DataService(IDatasetPersistenceService datasetPersistenceService)
    {
        _datasetPersistenceService = datasetPersistenceService ?? throw new ArgumentNullException(nameof(datasetPersistenceService));
        // The restored data came from the autosave file, so re-persisting it would
        // only rewrite the identical file we just read.
        RestorePersistedDataset(_datasetPersistenceService.TryLoadDataset(), persist: false);
    }

    public async Task LoadCsvAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        await LoadCsvFromStreamAsync(stream, path, cancellationToken);
    }

    public async Task LoadJsonAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var rows = new List<Dictionary<string, object?>>();

        if (path.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(stream);
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
        CommitDataset(path, headers, rows);
    }

    public async Task LoadCsvFromStreamAsync(Stream stream, string name, CancellationToken cancellationToken = default)
    {
        if (LooksLikeZipArchive(stream))
        {
            await LoadSpreadsheetFromStreamAsync(stream, name, cancellationToken);
            return;
        }

        try
        {
            (string[] headers, List<Dictionary<string, object?>> rows) = await ParseCsvAsync(stream, cancellationToken);
            CommitDataset(name, headers, rows);
        }
        catch (CsvHelperException ex)
        {
            throw new InvalidDataException("The selected CSV file could not be read as plain text. If it came from Excel, upload the workbook or export it as CSV UTF-8.", ex);
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

        CommitDataset(name, headers, rows);
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
        CommitDataset(name, headers, rows);
    }

    public IReadOnlyList<ColumnInfo> GetSchema() => _schema;

    public PersistedDatasetSnapshot? CreateSnapshot()
    {
        if (_rows.Count == 0 || string.IsNullOrWhiteSpace(DatasetName))
            return null;

        return BuildSnapshot(DatasetName, _schema, _rows);
    }

    private static PersistedDatasetSnapshot BuildSnapshot(
        string datasetName,
        List<ColumnInfo> schema,
        List<Dictionary<string, object?>> rows)
    {
        string[] headers = [.. schema.Select(column => column.Name)];
        return new PersistedDatasetSnapshot
        {
            DatasetName = datasetName,
            Headers = headers,
            Rows = [.. rows.Select(row => row.ToDictionary(
                kv => kv.Key,
                kv => SerializePersistedValue(kv.Value),
                StringComparer.OrdinalIgnoreCase))],
        };
    }

    public void LoadSnapshot(PersistedDatasetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RestorePersistedDataset(snapshot);
    }

    public void LoadRows(string name, string[] headers, List<Dictionary<string, object?>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        CommitDataset(name, headers, rows);
    }

    public void Clear()
    {
        DatasetName = null;
        _rows = [];
        _schema = [];
        NotifyChanged();
    }

    public string GetDataSummary()
    {
        if (_rows.Count == 0) return "(no dataset loaded)";
        var sb = new StringBuilder();
        sb.Append($"Dataset: {DatasetName}, {_rows.Count} rows. Columns: ");
        sb.Append(string.Join(", ", _schema.Select(c => $"{c.Name}:{c.Type.ToString().ToLowerInvariant()}")));
        sb.AppendLine();
        sb.AppendLine("Sample rows (up to 5):");
        foreach (var row in _rows.Take(5))
        {
            sb.AppendLine("  " + string.Join(" | ", row.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        return sb.ToString().TrimEnd();
    }

    public string GetColumnProfile()
    {
        if (_rows.Count == 0) return "(no dataset loaded)";

        var sb = new StringBuilder();
        sb.AppendLine("Column profiles (use these to pick good X/Y columns):");

        foreach (ColumnInfo column in _schema)
        {
            var values = _rows
                .Select(r => r.GetValueOrDefault(column.Name)?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .ToList();

            if (column.Type == ColumnType.Number)
            {
                var numbers = values
                    .Select(v => TryExtractNumericValue(v, out double n) ? (double?)n : null)
                    .Where(n => n is not null)
                    .Select(n => n!.Value)
                    .ToList();

                if (numbers.Count > 0)
                    sb.AppendLine($"  {column.Name} (number): range {numbers.Min():0.##} to {numbers.Max():0.##}, avg {numbers.Average():0.##}");
                else
                    sb.AppendLine($"  {column.Name} (number): no numeric values");
            }
            else
            {
                var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string typeLabel = column.Type.ToString().ToLowerInvariant();
                string samples = string.Join(", ", distinct.Take(8).Select(v => $"\"{v}\""));
                string suffix = distinct.Count > 8 ? ", …" : string.Empty;
                sb.AppendLine($"  {column.Name} ({typeLabel}): {distinct.Count} distinct values — {samples}{suffix}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    public IReadOnlyList<(string Label, double Value)> QuerySeries(string xColumn, string yColumn, Aggregation aggregation) =>
        QuerySeries(xColumn, yColumn, aggregation, []);

    public IReadOnlyList<(string Label, double Value)> QuerySeries(string xColumn, string yColumn, Aggregation aggregation, IReadOnlyList<DataFilter> filters) =>
        QuerySeriesWithStats(xColumn, yColumn, aggregation, filters).Points;

    public SeriesResult QuerySeriesWithStats(string xColumn, string yColumn, Aggregation aggregation, IReadOnlyList<DataFilter> filters)
    {
        IEnumerable<Dictionary<string, object?>> filtered = _rows.Where(r => r.ContainsKey(xColumn));

        if (filters is { Count: > 0 })
        {
            filtered = filtered.Where(r => filters.All(f =>
                !r.ContainsKey(f.Column) || f.Matches(r.GetValueOrDefault(f.Column))));
        }

        List<Dictionary<string, object?>> rows = [.. filtered];

        // Count the non-empty Y values that aren't numbers (blanks are expected and not reported).
        int ignored = 0;
        if (aggregation != Aggregation.Count && !string.IsNullOrWhiteSpace(yColumn))
        {
            foreach (Dictionary<string, object?> row in rows)
            {
                string? cell = row.GetValueOrDefault(yColumn)?.ToString();
                if (!string.IsNullOrWhiteSpace(cell) && !TryExtractNumericValue(cell, out _))
                    ignored++;
            }
        }

        var points = rows.GroupBy(r => r[xColumn]?.ToString() ?? "(null)").Select(g =>
        {
            if (aggregation == Aggregation.Count)
                return (g.Key, g.Count());

            // Aggregate only the cells that parse as numbers; blanks and stray text
            // would otherwise enter as 0 and silently skew averages, mins, and maxes.
            var numbers = g
                .Select(r => TryExtractNumericValue(r.GetValueOrDefault(yColumn)?.ToString(), out double n) ? (double?)n : null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList();

            if (numbers.Count == 0)
                return (g.Key, 0d);

            double value = aggregation switch
            {
                Aggregation.Sum => numbers.Sum(),
                Aggregation.Average => numbers.Average(),
                Aggregation.Min => numbers.Min(),
                Aggregation.Max => numbers.Max(),
                _ => numbers[0]
            };
            return (g.Key, value);
        });

        return new SeriesResult([.. points], ignored);
    }

    private static List<ColumnInfo> InferSchema(string[] headers, List<Dictionary<string, object?>> rows)
    {
        var schema = new List<ColumnInfo>();
        foreach (string h in headers)
        {
            var sampleValues = rows.Take(20)
                .Select(r => r.GetValueOrDefault(h)?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .ToList();
            schema.Add(new ColumnInfo(h, InferType(sampleValues)));
        }
        return schema;
    }

    /// <summary>
    /// Fraction of sampled values that must parse as numbers for the column to be treated as
    /// numeric. Below 1.0 so a mostly-numeric column (e.g. prices with a few "Free"/"Custom"
    /// entries) is still chartable; the non-numeric cells are skipped during aggregation.
    /// </summary>
    private const double NumericColumnThreshold = 0.5;

    private static ColumnType InferType(List<string> samples)
    {
        if (samples.Count == 0) return ColumnType.String;

        int numericCount = samples.Count(s => TryExtractNumericValue(s, out _));
        if (numericCount > 0 && numericCount >= samples.Count * NumericColumnThreshold)
            return ColumnType.Number;

        if (samples.All(s => DateTime.TryParse(s, out _)))
            return ColumnType.Date;

        return ColumnType.String;
    }

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

    private static async Task<(string[] Headers, List<Dictionary<string, object?>> Rows)> ParseCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        return await ParseCsvAsync(reader, cancellationToken);
    }

    private static async Task<(string[] Headers, List<Dictionary<string, object?>> Rows)> ParseCsvAsync(TextReader reader, CancellationToken cancellationToken)
    {
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
        return await ParseCsvAsync(reader, cancellationToken);
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

    private void CommitDataset(string name, string[] headers, List<Dictionary<string, object?>> rows, bool persist = true)
    {
        List<ColumnInfo> schema = InferSchema(headers, rows);
        DatasetName = Path.GetFileNameWithoutExtension(name);
        _rows = rows;
        _schema = schema;
        if (persist)
            PersistCurrentDatasetInBackground();
        NotifyChanged();
    }

    /// <summary>
    /// Builds the snapshot and writes the autosave file on a background thread so large
    /// datasets do not block the UI. Safe because committed row lists are replaced
    /// wholesale, never mutated in place. A newer commit supersedes any pending save.
    /// </summary>
    private void PersistCurrentDatasetInBackground()
    {
        string? datasetName = DatasetName;
        List<ColumnInfo> schema = _schema;
        List<Dictionary<string, object?>> rows = _rows;
        if (rows.Count == 0 || string.IsNullOrWhiteSpace(datasetName))
            return;

        int version = Interlocked.Increment(ref _persistVersion);
        _ = Task.Run(() =>
        {
            if (Volatile.Read(ref _persistVersion) != version)
                return;

            PersistedDatasetSnapshot snapshot = BuildSnapshot(datasetName, schema, rows);

            if (Volatile.Read(ref _persistVersion) != version)
                return;

            _datasetPersistenceService.SaveDataset(snapshot);
        });
    }

    private void RestorePersistedDataset(PersistedDatasetSnapshot? snapshot, bool persist = true)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.DatasetName) || snapshot.Headers.Length == 0)
            return;

        var rows = snapshot.Rows
            .Select(row => row.ToDictionary(
                kv => kv.Key,
                kv => (object?)kv.Value,
                StringComparer.OrdinalIgnoreCase))
            .ToList();

        CommitDataset(snapshot.DatasetName, snapshot.Headers, rows, persist);
    }

    private static string? SerializePersistedValue(object? value) => value switch
    {
        null => null,
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
        bool booleanValue => booleanValue ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private void NotifyChanged() => Changed?.Invoke();

    private static bool LooksLikeZipArchive(Stream stream)
    {
        long originalPosition = stream.Position;
        Span<byte> header = stackalloc byte[4];
        int bytesRead = stream.Read(header);
        stream.Position = originalPosition;

        return bytesRead == 4
            && header[0] == (byte)'P'
            && header[1] == (byte)'K'
            && header[2] == 0x03
            && header[3] == 0x04;
    }

    /// <summary>
    /// Parses a cell as a number, tolerating common value formatting: thousands separators,
    /// a currency-symbol prefix ("$1,299.99", "-$5"), and a percent suffix ("15%" → 15).
    /// Free text containing digits ("Order 1042") is intentionally NOT numeric — coercing
    /// it would silently misclassify identifier columns and skew aggregations.
    /// </summary>
    private static bool TryExtractNumericValue(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string normalized = text.Trim();

        bool negative = normalized.StartsWith('-');
        string unsigned = negative ? normalized[1..].TrimStart() : normalized;
        if (unsigned.Length > 1 && CurrencySymbols.Contains(unsigned[0]))
            normalized = (negative ? "-" : string.Empty) + unsigned[1..].TrimStart();

        if (normalized.Length > 1 && normalized.EndsWith('%'))
            normalized = normalized[..^1];

        return double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}