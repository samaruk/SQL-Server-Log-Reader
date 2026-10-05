using Microsoft.Data.SqlClient;
using SqlLogReader.Analysis;
using SqlLogReader.Log;
using SqlLogReader.Server;

namespace SqlLogReader.Tests.Integration;

/// <summary>
/// Runs only when SQLLOGREADER_TEST_CONNECTION holds a connection string for a sysadmin login, e.g.
/// <c>Server=localhost;User ID=sa;Password=...;TrustServerCertificate=True</c>.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(TestDatabase.ConnectionString))
            Skip = "Set SQLLOGREADER_TEST_CONNECTION to run tests against SQL Server.";
    }
}

/// <summary>A throw-away database in FULL recovery (with a full backup, so the log is kept until backed up).</summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("SQLLOGREADER_TEST_CONNECTION");

    private TestDatabase(string name) => Name = name;

    public string Name { get; }

    public ConnectionSettings Settings => new() { ConnectionString = ConnectionString, Database = Name };

    public static async Task<TestDatabase> CreateAsync(string prefix)
    {
        var db = new TestDatabase($"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, 120)]);
        await db.ExecuteOnMasterAsync($"CREATE DATABASE [{db.Name}]; ALTER DATABASE [{db.Name}] SET RECOVERY FULL; " +
                                      $"BACKUP DATABASE [{db.Name}] TO DISK = 'NUL';");
        return db;
    }

    public async Task ExecuteOnMasterAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await Settings.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql)
    {
        await using var connection = await Settings.OpenAsync();
        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Truncates the log (log backup to NUL) so that earlier operations are no longer in the online log.</summary>
    public Task TruncateLogAsync() =>
        ExecuteOnMasterAsync($"BACKUP LOG [{Name}] TO DISK = 'NUL'; USE [{Name}]; CHECKPOINT; BACKUP LOG [{Name}] TO DISK = 'NUL';");

    public async Task<LogReadResult> ReadLogAsync(AnalyzerOptions? analysis = null, LogSourceOptions? source = null)
    {
        var result = await LogReaderService.ReadAsync(new LogReadRequest
        {
            Connection = Settings,
            Database = Name,
            Analysis = analysis ?? new AnalyzerOptions(),
            Source = source ?? new LogSourceOptions(),
        });
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            SqlConnection.ClearAllPools();
            await ExecuteOnMasterAsync($"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
        }
        catch (SqlException)
        {
            // best effort
        }
    }
}
