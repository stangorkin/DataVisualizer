using DataVizAgent.Models;

namespace DataVizAgent.Services;

/// <summary>Outcome of validating an agent-supplied <see cref="ChartSpecRequest"/> against the loaded dataset.</summary>
public sealed record ChartSpecValidationResult(bool IsValid, ChartSpecRequest? Normalized, string? Error)
{
    public static ChartSpecValidationResult Valid(ChartSpecRequest normalized) => new(true, normalized, null);
    public static ChartSpecValidationResult Invalid(string error) => new(false, null, error);
}

/// <summary>
/// Validates a structured chart request against the current dataset schema and normalizes column
/// names to their canonical (case-correct) form so downstream querying is exact. Mirrors the rules
/// used by manual chart editing in <see cref="ReportSessionService"/>.
/// </summary>
public static class ChartSpecValidator
{
    public static ChartSpecValidationResult Validate(ChartSpecRequest request, IDataService dataService)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dataService);

        if (dataService.RowCount == 0)
            return ChartSpecValidationResult.Invalid("no dataset is loaded yet. Load a CSV or JSON file first.");

        var schema = dataService.GetSchema();
        if (schema.Count == 0)
            return ChartSpecValidationResult.Invalid("the loaded dataset has no columns to chart.");

        string availableColumns = string.Join(", ", schema.Select(c => c.Name));

        // X column is always required.
        var xColumn = FindColumn(schema, request.XColumn);
        if (xColumn is null)
            return ChartSpecValidationResult.Invalid(
                $"\"{request.XColumn}\" is not a column in this dataset. Available columns: {availableColumns}.");

        // Y column is required for every aggregation except Count.
        ColumnInfo? yColumn = null;
        if (RequiresYColumn(request.Aggregation))
        {
            yColumn = FindColumn(schema, request.YColumn);
            if (yColumn is null)
                return ChartSpecValidationResult.Invalid(
                    $"\"{request.YColumn}\" is not a column in this dataset. Available columns: {availableColumns}.");

            if (yColumn.Type != ColumnType.Number)
                return ChartSpecValidationResult.Invalid(
                    $"the {request.Aggregation} aggregation needs a numeric Y column, but \"{yColumn.Name}\" is {yColumn.Type}.");

            // Summing or averaging a per-row unique key produces huge meaningless totals that
            // read like real answers (observed: "15 trillion entries" from summing an event-ID
            // column). Refuse, and steer toward count — what such requests almost always mean.
            if (request.Aggregation is Aggregation.Sum or Aggregation.Average &&
                dataService.IsLikelyIdentifierColumn(yColumn.Name))
            {
                return ChartSpecValidationResult.Invalid(
                    $"\"{yColumn.Name}\" looks like a unique identifier (a different value on every row), " +
                    $"so {request.Aggregation.ToString().ToLowerInvariant()} of it would be meaningless. " +
                    "To count rows per group, use the count aggregation instead.");
            }
        }

        var normalized = new ChartSpecRequest
        {
            Type = request.Type,
            Title = string.IsNullOrWhiteSpace(request.Title)
                ? BuildDefaultTitle(request, xColumn, yColumn)
                : request.Title.Trim(),
            XColumn = xColumn.Name,
            YColumn = yColumn?.Name ?? string.Empty,
            Aggregation = request.Aggregation,
            Action = request.Action,
            Page = request.Page.Trim(),
            Filters = NormalizeFilters(request.Filters, schema),
            Sort = request.Sort,
            Limit = Math.Max(0, request.Limit),
            Reason = request.Reason.Trim(),
        };

        return ChartSpecValidationResult.Valid(normalized);
    }

    /// <summary>Keeps only filters that reference a real column, normalized to canonical column names.</summary>
    private static List<DataFilter> NormalizeFilters(List<DataFilter> filters, IReadOnlyList<ColumnInfo> schema)
    {
        if (filters is not { Count: > 0 })
            return [];

        var normalized = new List<DataFilter>();
        foreach (DataFilter filter in filters)
        {
            ColumnInfo? column = FindColumn(schema, filter.Column);
            if (column is not null)
                normalized.Add(filter with { Column = column.Name });
        }

        return normalized;
    }

    private static bool RequiresYColumn(Aggregation aggregation) => aggregation != Aggregation.Count;

    private static ColumnInfo? FindColumn(IReadOnlyList<ColumnInfo> schema, string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : schema.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string BuildDefaultTitle(ChartSpecRequest request, ColumnInfo xColumn, ColumnInfo? yColumn)
    {
        if (request.Aggregation == Aggregation.Count || yColumn is null)
            return $"Count by {xColumn.Name}";

        return $"{request.Aggregation} of {yColumn.Name} by {xColumn.Name}";
    }
}
