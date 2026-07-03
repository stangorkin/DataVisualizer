using System.Text.RegularExpressions;

namespace DataVizAgent.Services;

/// <summary>
/// Validates that a user-supplied SQL query is a single read-only statement before it is
/// executed by <see cref="DatabaseImportService"/>. This guards against accidental
/// modifications (e.g. pasting a script that ends in a DELETE), not against a malicious
/// user — the user supplies their own connection string, so it is their database.
/// </summary>
public static partial class ReadOnlyQueryGuard
{
    [GeneratedRegex(@"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReadOnlyStartPattern();

    /// <summary>Returns true when the query is a single SELECT (or WITH … SELECT) statement.</summary>
    public static bool Validate(string? query, out string? error)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            error = "A SELECT query is required.";
            return false;
        }

        if (!ReadOnlyStartPattern().IsMatch(query))
        {
            error = "Only SELECT queries are allowed (WITH … SELECT is also fine).";
            return false;
        }

        if (ContainsStatementSeparator(query))
        {
            error = "Only a single statement is allowed — remove everything after the first \";\".";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Scans for a ";" that separates statements, ignoring semicolons inside string
    /// literals, quoted identifiers, and comments. A trailing ";" is allowed.
    /// </summary>
    private static bool ContainsStatementSeparator(string query)
    {
        string trimmed = query.TrimEnd();
        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1];

        int i = 0;
        while (i < trimmed.Length)
        {
            char c = trimmed[i];

            switch (c)
            {
                case ';':
                    return true;
                case '\'' or '"':
                    i = SkipQuoted(trimmed, i, c);
                    continue;
                case '[': // SQL Server quoted identifier
                    i = SkipUntil(trimmed, i + 1, ']');
                    continue;
                case '-' when i + 1 < trimmed.Length && trimmed[i + 1] == '-':
                    i = SkipUntil(trimmed, i + 2, '\n');
                    continue;
                case '/' when i + 1 < trimmed.Length && trimmed[i + 1] == '*':
                    int end = trimmed.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? trimmed.Length : end + 2;
                    continue;
                default:
                    i++;
                    continue;
            }
        }

        return false;
    }

    /// <summary>Skips a quoted region starting at <paramref name="start"/>, honoring doubled-quote escapes.</summary>
    private static int SkipQuoted(string text, int start, char quote)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            if (text[i] == quote)
            {
                if (i + 1 < text.Length && text[i + 1] == quote)
                {
                    i += 2; // escaped quote ('' or "")
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static int SkipUntil(string text, int start, char terminator)
    {
        int end = text.IndexOf(terminator, start);
        return end < 0 ? text.Length : end + 1;
    }
}
