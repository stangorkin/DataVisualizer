using System.Globalization;

namespace DataVizAgent.Models;

/// <summary>Comparison used by a <see cref="DataFilter"/>.</summary>
public enum FilterOperator
{
    Equals,
    NotEquals,
    Contains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
}

/// <summary>A single row-level filter applied before aggregation (e.g. Year &gt;= 2024).</summary>
public sealed record DataFilter(string Column, FilterOperator Operator, string Value)
{
    /// <summary>Evaluates the filter against a single cell value.</summary>
    public bool Matches(object? cell)
    {
        string cellText = cell?.ToString() ?? string.Empty;

        return Operator switch
        {
            FilterOperator.Equals => string.Equals(cellText, Value, StringComparison.OrdinalIgnoreCase),
            FilterOperator.NotEquals => !string.Equals(cellText, Value, StringComparison.OrdinalIgnoreCase),
            FilterOperator.Contains => cellText.Contains(Value, StringComparison.OrdinalIgnoreCase),
            FilterOperator.GreaterThan => CompareNumeric(cellText) > 0,
            FilterOperator.GreaterThanOrEqual => CompareNumeric(cellText) >= 0,
            FilterOperator.LessThan => CompareNumeric(cellText) < 0,
            FilterOperator.LessThanOrEqual => CompareNumeric(cellText) <= 0,
            _ => true,
        };
    }

    /// <summary>Compares the cell against the filter value numerically when possible, else lexically.</summary>
    private int CompareNumeric(string cellText)
    {
        if (double.TryParse(cellText, NumberStyles.Any, CultureInfo.InvariantCulture, out double cellNumber) &&
            double.TryParse(Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double filterNumber))
        {
            return cellNumber.CompareTo(filterNumber);
        }

        return string.Compare(cellText, Value, StringComparison.OrdinalIgnoreCase);
    }

    public string Describe()
    {
        string op = Operator switch
        {
            FilterOperator.Equals => "=",
            FilterOperator.NotEquals => "≠",
            FilterOperator.Contains => "contains",
            FilterOperator.GreaterThan => ">",
            FilterOperator.GreaterThanOrEqual => "≥",
            FilterOperator.LessThan => "<",
            FilterOperator.LessThanOrEqual => "≤",
            _ => "?",
        };
        return $"{Column} {op} {Value}";
    }
}
