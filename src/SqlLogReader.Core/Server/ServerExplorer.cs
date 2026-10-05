using System.Data;
using Microsoft.Data.SqlClient;
using SqlLogReader.Schema;

namespace SqlLogReader.Server;

public sealed record ServerSummary(string ServerName, string Version, string Edition, bool IsSysAdmin);

public sealed record DatabaseSummary(
    string Name, int DatabaseId, string RecoveryModel, string State, string LogReuseWait, decimal LogSizeMb, decimal LogUsedPercent);

public sealed record LogBackupEntry(
    string DatabaseName, DateTime StartTime, DateTime FinishTime, string FirstLsn, string LastLsn, string PhysicalDeviceName, int Position);

public sealed record VirtualLogFile(long BeginOffset, decimal SizeMb, long SequenceNumber, bool IsActive, int Status, int Parity, string FirstLsn);

/// <summary>Server, database and backup information shown by the tools.</summary>
public static class ServerExplorer
{
    public static async Task<ServerSummary> GetServerSummaryAsync(SqlConnection connection, CancellationToken ct = default)
    {
        const string sql = """
            SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')),
                   CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                   CONVERT(nvarchar(256), SERVERPROPERTY('Edition')),
                   IS_SRVROLEMEMBER('sysadmin');
            """;
        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new ServerSummary(
            reader.IsDBNull(0) ? connection.DataSource : reader.GetString(0),
            reader.IsDBNull(1) ? "" : reader.GetString(1),
            reader.IsDBNull(2) ? "" : reader.GetString(2),
            !reader.IsDBNull(3) && reader.GetInt32(3) == 1);
    }

    public static async Task<List<DatabaseSummary>> ListDatabasesAsync(SqlConnection connection, CancellationToken ct = default)
    {
        const string sql = """
            SELECT d.name, d.database_id, d.recovery_model_desc, d.state_desc, d.log_reuse_wait_desc,
                   CONVERT(decimal(18, 2), ISNULL(ls.cntr_size, 0) / 1024.0),
                   CONVERT(decimal(9, 2), CASE WHEN ISNULL(ls.cntr_size, 0) = 0 THEN 0 ELSE 100.0 * ISNULL(lu.cntr_used, 0) / ls.cntr_size END)
            FROM sys.databases AS d
            OUTER APPLY (SELECT TOP (1) cntr_value AS cntr_size FROM sys.dm_os_performance_counters
                         WHERE counter_name = 'Log File(s) Size (KB)' AND instance_name = d.name) AS ls
            OUTER APPLY (SELECT TOP (1) cntr_value AS cntr_used FROM sys.dm_os_performance_counters
                         WHERE counter_name = 'Log File(s) Used Size (KB)' AND instance_name = d.name) AS lu
            ORDER BY CASE WHEN d.database_id <= 4 THEN 1 ELSE 0 END, d.name;
            """;
        var list = new List<DatabaseSummary>();
        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new DatabaseSummary(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetDecimal(5), reader.GetDecimal(6)));
        }
        return list;
    }

    /// <summary>Transaction log backups recorded in msdb for a database, newest last.</summary>
    public static async Task<List<LogBackupEntry>> GetLogBackupHistoryAsync(SqlConnection connection, string databaseName, CancellationToken ct = default)
    {
        const string sql = """
            SELECT bs.database_name, bs.backup_start_date, bs.backup_finish_date,
                   CONVERT(nvarchar(30), bs.first_lsn), CONVERT(nvarchar(30), bs.last_lsn),
                   bmf.physical_device_name, bs.position
            FROM msdb.dbo.backupset AS bs
            JOIN msdb.dbo.backupmediafamily AS bmf ON bmf.media_set_id = bs.media_set_id
            WHERE bs.type = 'L' AND bs.database_name = @db
            ORDER BY bs.backup_start_date;
            """;
        var list = new List<LogBackupEntry>();
        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.Add(new SqlParameter("@db", SqlDbType.NVarChar, 128) { Value = databaseName });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new LogBackupEntry(reader.GetString(0), reader.GetDateTime(1), reader.GetDateTime(2),
                FormatLsn(reader.IsDBNull(3) ? null : reader.GetString(3)), FormatLsn(reader.IsDBNull(4) ? null : reader.GetString(4)),
                reader.IsDBNull(5) ? "" : reader.GetString(5), reader.IsDBNull(6) ? 1 : Convert.ToInt32(reader.GetValue(6))));
        }
        return list;
    }

    private static string FormatLsn(string? numeric) =>
        Lsn.TryParseAny(numeric, out var lsn) ? lsn.ToString() : numeric ?? "";

    /// <summary>Virtual log files of the current database (sys.dm_db_log_info, SQL Server 2016 SP2+).</summary>
    public static async Task<List<VirtualLogFile>> GetVirtualLogFilesAsync(SqlConnection connection, CancellationToken ct = default)
    {
        const string sql = """
            SELECT vlf_begin_offset, CONVERT(decimal(18, 2), vlf_size_mb), vlf_sequence_number, vlf_active, vlf_status, vlf_parity, vlf_first_lsn
            FROM sys.dm_db_log_info(DB_ID()) ORDER BY vlf_begin_offset;
            """;
        var list = new List<VirtualLogFile>();
        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new VirtualLogFile(reader.GetInt64(0), reader.GetDecimal(1), Convert.ToInt64(reader.GetValue(2)),
                reader.GetBoolean(3), Convert.ToInt32(reader.GetValue(4)), Convert.ToInt32(reader.GetValue(5)),
                reader.IsDBNull(6) ? "" : reader.GetString(6)));
        }
        return list;
    }

    /// <summary>Resolves login names for the SIDs of records read without SQL Server (offline .ldf).</summary>
    public static async Task ResolveUserNamesAsync(SqlConnection connection, IEnumerable<Log.LogRecord> records, CancellationToken ct = default)
    {
        var cache = new Dictionary<string, string?>();
        foreach (var record in records)
        {
            if (record.TransactionSid is not { Length: > 0 } sid || record.UserName != null) continue;
            var key = Convert.ToHexString(sid);
            if (!cache.TryGetValue(key, out var name))
            {
                await using var cmd = new SqlCommand("SELECT SUSER_SNAME(@sid);", connection);
                cmd.Parameters.Add(new SqlParameter("@sid", SqlDbType.VarBinary, 85) { Value = sid });
                var value = await cmd.ExecuteScalarAsync(ct);
                cache[key] = name = value as string;
            }
            record.UserName = name;
        }
    }

    /// <summary>
    /// Attaches a database from its data (.mdf) and log (.ldf) files. The paths are on the SQL Server machine
    /// and the SQL Server service account needs read/write access to them. Work on copies of the files:
    /// attaching runs crash recovery, which writes to the log.
    /// </summary>
    public static async Task AttachDatabaseAsync(SqlConnection masterConnection, string databaseName, string mdfPath,
        IReadOnlyList<string> logPaths, CancellationToken ct = default)
    {
        var files = new List<string> { mdfPath };
        files.AddRange(logPaths.Where(p => !string.IsNullOrWhiteSpace(p)));
        // CREATE DATABASE does not accept variables for file names, so the paths are embedded as escaped literals.
        var sql = $"CREATE DATABASE {TableInfo.Quote(databaseName)} ON " +
                  string.Join(", ", files.Select(f => "(FILENAME = N'" + f.Replace("'", "''") + "')")) + " FOR ATTACH;";
        await using var cmd = new SqlCommand(sql, masterConnection) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
