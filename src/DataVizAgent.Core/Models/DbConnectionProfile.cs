namespace DataVizAgent.Models;

/// <summary>Supported database provider types.</summary>
public enum DbProvider { SQLite, SqlServer, PostgreSQL }

/// <summary>All the information needed to connect to a database and run a query.</summary>
public sealed class DbConnectionProfile
{
    public DbProvider Provider { get; set; } = DbProvider.SQLite;

    /// <summary>ADO.NET connection string for the selected provider.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>SELECT statement to execute. Only SELECT is permitted.</summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>Friendly name used as the dataset name; defaults to the query table name when empty.</summary>
    public string DatasetName { get; set; } = string.Empty;

    /// <summary>Maximum rows to import (0 = no limit). Prevents accidental memory exhaustion.</summary>
    public int MaxRows { get; set; } = 10_000;
}
