using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataVizAgent.Models;

namespace DataVizAgent.Services;

/// <summary>
/// Extracts a structured chart request from an agent response. The agent is asked to emit a
/// JSON "tool call" describing the chart it wants to create. To stay robust against the quirks
/// of local GGUF models, this parser accepts several shapes:
///   1. A fenced <c>```chart { ... }```</c> block (preferred / documented format).
///   2. A fenced <c>```json { ... }```</c> block.
///   3. A tool-call wrapper such as <c>{ "name": "create_chart", "arguments": { ... } }</c>.
///   4. A bare JSON object containing chart fields anywhere in the text.
/// </summary>
public static partial class ChartSpecParser
{
    // Matches any fenced code block whose body is a JSON object: ```lang { ... } ```
    [GeneratedRegex(@"```(?<lang>[a-zA-Z]*)\s*(?<body>\{.*?\})\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FencedJsonBlockRegex();

    // Matches a complete fenced block explicitly tagged as a tool call, whether or not its body
    // is valid JSON — these are never chat prose and must never be shown to the user raw.
    [GeneratedRegex(@"```(chart|query)\b.*?```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TaggedToolBlockRegex();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new TolerantEnumConverterFactory() }
    };

    /// <summary>
    /// Extracts the first chart request that contains usable chart fields, or <c>null</c> when none is present.
    /// </summary>
    public static ChartSpecRequest? TryParse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        foreach (var candidate in EnumerateCandidates(responseText))
        {
            if (TryDeserialize(candidate, out var request) && request is not null && request.HasChartFields)
                return request;
        }

        return null;
    }

    /// <summary>Removes chart tool-call JSON from the text so the remaining prose can be shown to the user.</summary>
    public static string StripChartBlocks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Tool-tagged fences are always tool calls, never prose — drop them even when their JSON
        // is malformed or unsupported, so a bad block can never leak into chat as raw text.
        string stripped = TaggedToolBlockRegex().Replace(text, string.Empty);

        // Remove other fenced blocks (```json …) only when they actually contain a chart request.
        stripped = FencedJsonBlockRegex().Replace(stripped, match =>
            TryDeserialize(match.Groups["body"].Value, out var request) && request?.HasChartFields == true
                ? string.Empty
                : match.Value);

        // Remove any remaining bare JSON object that is a chart request.
        foreach (var candidate in EnumerateBareJsonObjects(stripped))
        {
            if (TryDeserialize(candidate, out var request) && request?.HasChartFields == true)
                stripped = stripped.Replace(candidate, string.Empty);
        }

        return stripped.Trim();
    }

    /// <summary>
    /// Removes a trailing fenced block that was opened but never closed — the signature of a
    /// generation cut off by its token cap mid tool call. Without this, the half-written JSON
    /// fragment would be shown to the user as chat text. Returns <c>true</c> when a fragment was
    /// removed; <paramref name="cleaned"/> then holds only the text before the dangling fence.
    /// </summary>
    public static bool TryStripUnclosedFencedBlock(string text, out string cleaned)
    {
        cleaned = text;
        if (string.IsNullOrEmpty(text))
            return false;

        int fenceCount = 0;
        int lastFence = -1;
        int index = 0;
        while ((index = text.IndexOf("```", index, StringComparison.Ordinal)) >= 0)
        {
            fenceCount++;
            lastFence = index;
            index += 3;
        }

        // An even fence count means every block closed; odd means the last fence opened one
        // that never ended before generation stopped.
        if (fenceCount % 2 == 0)
            return false;

        cleaned = text[..lastFence].TrimEnd('`').Trim();
        return true;
    }

    /// <summary>True when the text contains a complete fenced <c>```chart</c> / <c>```query</c> block, parseable or not.</summary>
    public static bool ContainsToolBlock(string text) =>
        !string.IsNullOrEmpty(text) && TaggedToolBlockRegex().IsMatch(text);

    private static IEnumerable<string> EnumerateCandidates(string text)
    {
        foreach (Match match in FencedJsonBlockRegex().Matches(text))
            yield return match.Groups["body"].Value;

        foreach (var bare in EnumerateBareJsonObjects(text))
            yield return bare;
    }

    /// <summary>Yields balanced top-level <c>{ ... }</c> substrings found in the text.</summary>
    private static IEnumerable<string> EnumerateBareJsonObjects(string text)
    {
        int depth = 0;
        int start = -1;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
            {
                if (depth == 0)
                    start = i;
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return text.Substring(start, i - start + 1);
                    start = -1;
                }
            }
        }
    }

    private static bool TryDeserialize(string json, out ChartSpecRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // Unwrap a tool-call wrapper: { "name"/"tool", "arguments"/"parameters": { ... } }.
            if (root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("arguments", out var args) || root.TryGetProperty("parameters", out args)) &&
                args.ValueKind == JsonValueKind.Object)
            {
                request = args.Deserialize<ChartSpecRequest>(_jsonOptions);
                return request is not null;
            }

            request = root.Deserialize<ChartSpecRequest>(_jsonOptions);
            return request is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Structured chart request emitted by the agent — columns, aggregation, and a short rationale.
/// Contains no computed data; <see cref="ChartDataComputer"/> turns it into a <see cref="ChartSpec"/>.
/// </summary>
public sealed class ChartSpecRequest
{
    public ChartType Type { get; set; } = ChartType.Bar;
    public string Title { get; set; } = string.Empty;
    public string XColumn { get; set; } = string.Empty;
    public string YColumn { get; set; } = string.Empty;
    public Aggregation Aggregation { get; set; } = Aggregation.None;

    /// <summary>Whether to add a new chart or update the user's currently selected chart.</summary>
    public ChartAction Action { get; set; } = ChartAction.Create;

    /// <summary>Optional target page name. If it does not exist, a new page is created.</summary>
    public string Page { get; set; } = string.Empty;

    /// <summary>Optional row-level filters applied before aggregation.</summary>
    public List<DataFilter> Filters { get; set; } = [];

    /// <summary>Optional ordering of the computed groups by value.</summary>
    public SortDirection Sort { get; set; } = SortDirection.None;

    /// <summary>Optional cap on the number of groups shown (0 = all). "Top N" requests set this.</summary>
    public int Limit { get; set; }

    /// <summary>Optional one-line rationale from the agent explaining the chart choice.</summary>
    public string Reason { get; set; } = string.Empty;

    [JsonIgnore]
    internal bool HasChartFields => !string.IsNullOrWhiteSpace(XColumn);
}