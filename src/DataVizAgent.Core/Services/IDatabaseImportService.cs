using DataVizAgent.Models;

namespace DataVizAgent.Services;

/// <summary>Result of a database import operation.</summary>
public sealed class DatabaseImportResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }
    public string[] Headers { get; private init; } = [];
    public List<Dictionary<string, object?>> Rows { get; private init; } = [];
    public bool WasTruncated { get; private init; }

    public static DatabaseImportResult Ok(string[] headers, List<Dictionary<string, object?>> rows, bool wasTruncated = false) =>
        new() { Success = true, Headers = headers, Rows = rows, WasTruncated = wasTruncated };

    public static DatabaseImportResult Fail(string error) =>
        new() { Success = false, Error = error };
}

public interface IDatabaseImportService
{
    /// <summary>Attempts to open a connection to verify the credentials are correct.</summary>
    Task<(bool Success, string? Error)> TestConnectionAsync(DbConnectionProfile profile, CancellationToken ct = default);

    /// <summary>Executes the profile's SELECT query and returns all result rows.</summary>
    Task<DatabaseImportResult> ImportAsync(DbConnectionProfile profile, CancellationToken ct = default);
}
