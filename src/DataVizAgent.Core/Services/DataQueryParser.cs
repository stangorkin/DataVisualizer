using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataVizAgent.Models;

namespace DataVizAgent.Services;

/// <summary>Sort order requested for a data query result.</summary>
public enum SortDirection { None, Asc, Desc }

/// <summary>A request from the agent to compute an aggregation so it can answer a question in prose.</summary>
public sealed class DataQueryRequest
{
    public string XColumn { get; set; } = string.Empty;
    public string YColumn { get; set; } = string.Empty;
    public Aggregation Aggregation { get; set; } = Aggregation.None;
    public SortDirection Sort { get; set; } = SortDirection.None;
    public int Limit { get; set; }
    public List<DataFilter> Filters { get; set; } = [];

    [JsonIgnore]
    internal bool HasQueryFields => !string.IsNullOrWhiteSpace(XColumn);
}

/// <summary>Extracts a <see cref="DataQueryRequest"/> from a fenced <c>```query { ... }```</c> block.</summary>
public static partial class DataQueryParser
{
    [GeneratedRegex(@"```query\s*(?<body>\{.*?\})\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex QueryBlockRegex();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new TolerantEnumConverterFactory() }
    };

    public static DataQueryRequest? TryParse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        foreach (Match match in QueryBlockRegex().Matches(responseText))
        {
            try
            {
                var request = JsonSerializer.Deserialize<DataQueryRequest>(match.Groups["body"].Value, _jsonOptions);
                if (request is not null && request.HasQueryFields)
                    return request;
            }
            catch (JsonException)
            {
                // Ignore malformed blocks and keep scanning.
            }
        }

        return null;
    }
}

/// <summary>Runs a <see cref="DataQueryRequest"/> against the dataset and formats the result for the agent.</summary>
public static class DataQueryRunner
{
    public static string Run(DataQueryRequest request, IDataService dataService)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dataService);

        // Reuse chart validation to normalize column names and enforce numeric-Y rules.
        var asChart = new ChartSpecRequest
        {
            XColumn = request.XColumn,
            YColumn = request.YColumn,
            Aggregation = request.Aggregation,
            Filters = request.Filters,
        };

        var validation = ChartSpecValidator.Validate(asChart, dataService);
        if (!validation.IsValid)
            return $"The data query could not be run because {validation.Error}";

        ChartSpecRequest normalized = validation.Normalized!;
        SeriesResult queryResult = dataService.QuerySeriesWithStats(normalized.XColumn, normalized.YColumn, normalized.Aggregation, normalized.Filters);
        var series = queryResult.Points.ToList();
        if (series.Count == 0)
            return "The data query returned no rows.";

        IEnumerable<(string Label, double Value)> ordered = request.Sort switch
        {
            SortDirection.Asc => series.OrderBy(s => s.Value),
            SortDirection.Desc => series.OrderByDescending(s => s.Value),
            _ => series,
        };

        int totalGroups = series.Count;
        if (request.Limit > 0)
            ordered = ordered.Take(request.Limit);

        var rows = ordered.ToList();

        string measure = normalized.Aggregation == Aggregation.Count
            ? "count"
            : $"{normalized.Aggregation.ToString().ToLowerInvariant()} of {normalized.YColumn}";

        var sb = new StringBuilder();
        sb.Append($"Computed {measure} by {normalized.XColumn}");
        if (normalized.Filters.Count > 0)
            sb.Append($" where {string.Join(" and ", normalized.Filters.Select(f => f.Describe()))}");
        if (request.Sort != SortDirection.None)
            sb.Append(request.Sort == SortDirection.Desc ? ", highest first" : ", lowest first");
        if (request.Limit > 0 && request.Limit < totalGroups)
            sb.Append($" (top {request.Limit} of {totalGroups})");
        sb.AppendLine(":");

        foreach (var (label, value) in rows)
            sb.AppendLine($"  {label}: {value.ToString("0.##", CultureInfo.InvariantCulture)}");

        if (queryResult.IgnoredNonNumericCount > 0)
            sb.AppendLine($"(Note: {queryResult.IgnoredNonNumericCount} non-numeric value(s) in {normalized.YColumn} were ignored.)");

        return sb.ToString().TrimEnd();
    }
}
