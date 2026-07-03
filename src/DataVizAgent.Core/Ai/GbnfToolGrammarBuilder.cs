using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace DataVizAgent.Ai;

/// <summary>
/// Generates a GBNF (llama.cpp grammar) that constrains the model's output to either free prose
/// or a single well-formed <c>```tool_call```</c> block matching one of the supplied tools'
/// JSON schemas. With this grammar applied during sampling, the model literally cannot emit a
/// malformed tool call — wrong key names, unquoted values, an unknown tool name, or a number where
/// a string is expected all become ungrammatical and are masked out token by token.
///
/// Scope: handles flat object schemas whose properties are string / integer / number / boolean,
/// plus string enums. That covers <see cref="ChartTools"/>; nested objects and arrays fall back to
/// prompted-only tool calling (the builder returns null and the caller skips the grammar).
/// </summary>
internal static class GbnfToolGrammarBuilder
{
    /// <summary>Builds the grammar, or returns null if any tool schema is outside the supported subset.</summary>
    public static string? TryBuild(IReadOnlyList<AIFunction> tools)
    {
        if (tools.Count == 0)
            return null;

        var callRules = new List<string>();
        var toolRules = new StringBuilder();

        foreach (AIFunction tool in tools)
        {
            string? rule = TryBuildCallRule(tool, out string ruleName);
            if (rule is null)
                return null; // unsupported schema shape — let the caller fall back to prompted-only

            callRules.Add(ruleName);
            toolRules.Append(rule);
        }

        var sb = new StringBuilder();
        sb.AppendLine("root ::= ws ( toolcall | freetext )");
        sb.AppendLine("freetext ::= [^`]*");
        sb.AppendLine("toolcall ::= \"```tool_call\" ws call ws \"```\"");
        sb.AppendLine($"call ::= {string.Join(" | ", callRules)}");
        sb.Append(toolRules);

        // Shared primitive rules.
        sb.AppendLine("ws ::= [ \\t\\n]*");
        sb.AppendLine("str ::= \"\\\"\" strchar* \"\\\"\"");
        sb.AppendLine("strchar ::= [^\"\\\\] | \"\\\\\" [\"\\\\/bfnrt]");
        sb.AppendLine("int ::= \"-\"? [0-9]+");
        sb.AppendLine("num ::= \"-\"? [0-9]+ (\".\" [0-9]+)?");
        sb.AppendLine("bool ::= \"true\" | \"false\"");

        return sb.ToString();
    }

    private static string? TryBuildCallRule(AIFunction tool, out string ruleName)
    {
        ruleName = RuleId(tool.Name) + "-call";

        JsonElement schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out JsonElement properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return null;

        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out JsonElement requiredArray) && requiredArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in requiredArray.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    required.Add(item.GetString()!);
        }

        // Partition properties (preserving schema order) into required-first then optional.
        var requiredProps = new List<(string Key, string Value)>();
        var optionalProps = new List<(string Key, string Value)>();

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            string? valueRule = ValueRuleFor(property.Value);
            if (valueRule is null)
                return null; // unsupported property type

            (required.Contains(property.Name) ? requiredProps : optionalProps).Add((property.Name, valueRule));
        }

        if (requiredProps.Count == 0)
        {
            // JSON comma placement assumes at least one mandatory property; promote the first.
            if (optionalProps.Count == 0)
                return null;
            requiredProps.Add(optionalProps[0]);
            optionalProps.RemoveAt(0);
        }

        var args = new StringBuilder();
        for (int i = 0; i < requiredProps.Count; i++)
        {
            if (i > 0) args.Append(" ws \",\" ws ");
            args.Append(KeyValue(requiredProps[i].Key, requiredProps[i].Value));
        }
        foreach ((string key, string value) in optionalProps)
            args.Append($" ( ws \",\" ws {KeyValue(key, value)} )?");

        string nameLiteral = Literal($"\"{tool.Name}\"");
        string rule =
            $"{ruleName} ::= \"{{\" ws {Literal("\"name\"")} ws \":\" ws {nameLiteral} ws \",\" ws " +
            $"{Literal("\"arguments\"")} ws \":\" ws \"{{\" ws {args} ws \"}}\" ws \"}}\"";

        return rule + "\n";
    }

    private static string KeyValue(string key, string valueRule) =>
        $"{Literal($"\"{key}\"")} ws \":\" ws {valueRule}";

    /// <summary>Maps a JSON Schema property to a GBNF value rule, or null if its type is unsupported.</summary>
    private static string? ValueRuleFor(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object)
            return null;

        // A string enum becomes an alternation of exact quoted literals.
        if (property.TryGetProperty("enum", out JsonElement enumValues) && enumValues.ValueKind == JsonValueKind.Array)
        {
            var literals = new List<string>();
            foreach (JsonElement value in enumValues.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String)
                    return null;
                literals.Add(Literal($"\"{value.GetString()}\""));
            }

            return literals.Count > 0 ? $"( {string.Join(" | ", literals)} )" : null;
        }

        return ExtractType(property) switch
        {
            "string" => "str",
            "integer" => "int",
            "number" => "num",
            "boolean" => "bool",
            _ => null,
        };
    }

    /// <summary>Reads the JSON Schema "type", tolerating a <c>["string","null"]</c>-style union.</summary>
    private static string? ExtractType(JsonElement property)
    {
        if (!property.TryGetProperty("type", out JsonElement type))
            return "string"; // untyped parameter — treat as free string

        if (type.ValueKind == JsonValueKind.String)
            return type.GetString();

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement entry in type.EnumerateArray())
            {
                string? name = entry.GetString();
                if (name is not null and not "null")
                    return name;
            }
        }

        return null;
    }

    /// <summary>Emits a GBNF double-quoted terminal that matches the literal text <paramref name="text"/>.</summary>
    private static string Literal(string text) =>
        "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string RuleId(string name) =>
        new(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
