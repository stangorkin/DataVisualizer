using DataVizAgent.Services;
using Xunit;

namespace DataVizAgent.Core.Tests;

public class ReadOnlyQueryGuardTests
{
    [Theory]
    [InlineData("SELECT * FROM orders")]
    [InlineData("  select id, name from customers  ")]
    [InlineData("SELECT * FROM orders;")]
    [InlineData("WITH totals AS (SELECT region, SUM(sales) s FROM orders GROUP BY region) SELECT * FROM totals")]
    [InlineData("SELECT * FROM notes WHERE body = 'a; b'")]
    [InlineData("SELECT * FROM notes WHERE body = 'it''s; fine'")]
    [InlineData("SELECT \"weird;column\" FROM t")]
    [InlineData("SELECT [weird;column] FROM t")]
    [InlineData("SELECT 1 -- trailing comment; with semicolon")]
    [InlineData("SELECT 1 /* block; comment */ FROM t")]
    public void Validate_AllowsSingleReadOnlyStatements(string query)
    {
        Assert.True(ReadOnlyQueryGuard.Validate(query, out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("DELETE FROM orders")]
    [InlineData("DROP TABLE orders")]
    [InlineData("INSERT INTO orders VALUES (1)")]
    [InlineData("UPDATE orders SET total = 0")]
    [InlineData("SELECT 1; DROP TABLE orders")]
    [InlineData("SELECT 1;DELETE FROM orders;")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x; DROP TABLE orders")]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsWritesAndBatches(string query)
    {
        Assert.False(ReadOnlyQueryGuard.Validate(query, out string? error));
        Assert.NotNull(error);
    }
}
