using System.Data.Common;
using DataVizAgent.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DataVizAgent.Services;

internal sealed class DatabaseImportService : IDatabaseImportService
{
    public async Task<(bool Success, string? Error)> TestConnectionAsync(DbConnectionProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.ConnectionString))
            return (false, "Connection string is required.");

        try
        {
            await using DbConnection conn = CreateConnection(profile);
            await conn.OpenAsync(ct);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<DatabaseImportResult> ImportAsync(DbConnectionProfile profile, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profile.ConnectionString))
            return DatabaseImportResult.Fail("Connection string is required.");

        if (!ReadOnlyQueryGuard.Validate(profile.Query, out string? queryError))
            return DatabaseImportResult.Fail(queryError!);

        try
        {
            await using DbConnection conn = CreateConnection(profile);
            await conn.OpenAsync(ct);

            await using DbCommand cmd = conn.CreateCommand();
            cmd.CommandText = profile.Query;
            cmd.CommandTimeout = 60;

            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct);

            string[] headers = new string[reader.FieldCount];
            for (int i = 0; i < reader.FieldCount; i++)
                headers[i] = string.IsNullOrWhiteSpace(reader.GetName(i)) ? $"Column{i + 1}" : reader.GetName(i);

            var rows = new List<Dictionary<string, object?>>();
            int limit = profile.MaxRows > 0 ? profile.MaxRows : int.MaxValue;
            bool truncated = false;

            while (await reader.ReadAsync(ct))
            {
                if (rows.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < headers.Length; i++)
                    row[headers[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);

                rows.Add(row);
            }

            return DatabaseImportResult.Ok(headers, rows, truncated);
        }
        catch (Exception ex)
        {
            return DatabaseImportResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Opens the connection read-only where the provider supports it, so the "no
    /// modifications" promise holds even if a write statement slips past the query guard.
    /// SQL Server has no equivalent connection-level switch and relies on the guard alone.
    /// </summary>
    private static DbConnection CreateConnection(DbConnectionProfile profile) =>
        profile.Provider switch
        {
            DbProvider.SQLite => new SqliteConnection(
                new SqliteConnectionStringBuilder(profile.ConnectionString)
                {
                    Mode = SqliteOpenMode.ReadOnly,
                }.ToString()),
            DbProvider.SqlServer => new SqlConnection(profile.ConnectionString),
            DbProvider.PostgreSQL => new NpgsqlConnection(
                new NpgsqlConnectionStringBuilder(profile.ConnectionString)
                {
                    Options = AppendOption(
                        new NpgsqlConnectionStringBuilder(profile.ConnectionString).Options,
                        "-c default_transaction_read_only=on"),
                }.ToString()),
            _ => throw new ArgumentOutOfRangeException(nameof(profile.Provider), profile.Provider, null),
        };

    private static string AppendOption(string? existingOptions, string option) =>
        string.IsNullOrWhiteSpace(existingOptions) ? option : $"{existingOptions} {option}";
}
