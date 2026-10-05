using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlLogReader.Log;

/// <summary>Reads log records from SQL Server with <c>fn_dblog</c> (online log) and <c>fn_dump_dblog</c> (log backups).</summary>
public static class SqlLogSource
{
    private const string SelectList = """
        SELECT [Current LSN], [Previous LSN], [Operation], [Context], [Transaction ID],
               [AllocUnitId], [AllocUnitName], [Page ID], [Slot ID], [Offset in Row], [Modify Size],
               [Transaction Name], [Begin Time], [End Time], [Transaction SID],
               CASE WHEN [Operation] = N'LOP_BEGIN_XACT' THEN SUSER_SNAME([Transaction SID]) END AS [User Name],
               [Description], [Lock Information],
               CASE WHEN [Operation] IN (N'LOP_INSERT_ROWS', N'LOP_DELETE_ROWS', N'LOP_MODIFY_ROW', N'LOP_MODIFY_COLUMNS')
                    THEN [Log Record] END AS [Log Record],
               [RowLog Contents 0], [RowLog Contents 1], [RowLog Contents 2], [RowLog Contents 3], [RowLog Contents 4]
        """;

    private const string WhereClause = """
        WHERE ([Operation] IN (N'LOP_BEGIN_XACT', N'LOP_COMMIT_XACT', N'LOP_ABORT_XACT')
           OR ([Operation] IN (N'LOP_INSERT_ROWS', N'LOP_DELETE_ROWS', N'LOP_MODIFY_ROW', N'LOP_MODIFY_COLUMNS')
               AND [Context] IN (N'LCX_HEAP', N'LCX_CLUSTERED', N'LCX_MARK_AS_GHOST')))
        """;

    private const string RawSelectList = """
        SELECT [Current LSN], [Previous LSN], [Operation], [Context], [Transaction ID],
               [AllocUnitId], [AllocUnitName], [Page ID], [Slot ID], [Offset in Row], [Modify Size],
               [Transaction Name], [Begin Time], [End Time], [Transaction SID],
               CASE WHEN [Operation] = N'LOP_BEGIN_XACT' THEN SUSER_SNAME([Transaction SID]) END AS [User Name],
               [Description], [Lock Information], [Log Record],
               [RowLog Contents 0], [RowLog Contents 1], [RowLog Contents 2], [RowLog Contents 3], [RowLog Contents 4]
        """;

    /// <summary>
    /// Reads the records needed to decode row changes (transaction begin/commit/abort and row operations
    /// on heaps and clustered indexes) from every configured source, merged in LSN order.
    /// </summary>
    public static async Task<List<LogRecord>> ReadAsync(SqlConnection connection, LogSourceOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var all = new List<LogRecord>();
        foreach (var backup in options.BackupFiles.Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            var files = backup.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var positions = await GetLogBackupPositionsAsync(connection, files, cancellationToken);
            foreach (var position in positions)
            {
                progress?.Report($"Reading log backup {string.Join(", ", files)} (backup set {position})...");
                var sql = new StringBuilder(SelectList).AppendLine()
                    .Append("FROM sys.fn_dump_dblog(NULL, NULL, N'DISK', ").Append(position).Append(", ");
                var parameters = new List<SqlParameter>();
                for (int i = 0; i < 64; i++)
                {
                    if (i < files.Length)
                    {
                        sql.Append("@f").Append(i);
                        parameters.Add(new SqlParameter("@f" + i, SqlDbType.NVarChar, 260) { Value = files[i] });
                    }
                    else
                    {
                        sql.Append("DEFAULT");
                    }
                    sql.Append(i < 63 ? ", " : ")");
                }
                sql.AppendLine().Append(WhereClause).Append(LsnRangeFilter(options, parameters));
                await ReadRecordsAsync(connection, sql.ToString(), parameters, $"backup:{files[0]}#{position}", all, options, progress, cancellationToken);
            }
        }

        if (options.IncludeOnlineLog)
        {
            progress?.Report("Reading the online transaction log...");
            if (options.IncludeInactiveLog)
                await ExecuteAsync(connection, "DBCC TRACEON(2537) WITH NO_INFOMSGS;", cancellationToken);
            try
            {
                var parameters = new List<SqlParameter>();
                var sql = SelectList + "\nFROM sys.fn_dblog(NULL, NULL)\n" + WhereClause + LsnRangeFilter(options, parameters);
                await ReadRecordsAsync(connection, sql, parameters, "online", all, options, progress, cancellationToken);
            }
            finally
            {
                if (options.IncludeInactiveLog)
                    await ExecuteAsync(connection, "DBCC TRACEOFF(2537) WITH NO_INFOMSGS;", CancellationToken.None);
            }
        }

        // Merge sources in LSN order; a record can be both in a log backup and still in the online log.
        var merged = all.OrderBy(r => r.Lsn).ToList();
        var result = new List<LogRecord>(merged.Count);
        foreach (var r in merged)
            if (result.Count == 0 || result[^1].Lsn != r.Lsn)
                result.Add(r);
        return result;
    }

    /// <summary>Returns every log record (no filtering) of the online log, for raw inspection.</summary>
    public static async Task<List<LogRecord>> ReadRawAsync(SqlConnection connection, int top, LogSourceOptions options,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<SqlParameter>();
        var filter = LsnRangeFilter(options, parameters);
        var sql = RawSelectList.Replace("SELECT ", top > 0 ? $"SELECT TOP ({top}) " : "SELECT ")
                  + "\nFROM sys.fn_dblog(NULL, NULL)\nWHERE 1 = 1" + filter;
        var list = new List<LogRecord>();
        await ReadRecordsAsync(connection, sql, parameters, "online", list, options, null, cancellationToken);
        return list;
    }

    private static string LsnRangeFilter(LogSourceOptions options, List<SqlParameter> parameters)
    {
        var sb = new StringBuilder();
        if (options.StartLsn is Lsn start)
        {
            // fn_dblog prints the LSN in upper or lower case depending on the version: compare upper-cased text.
            sb.Append("\n  AND UPPER([Current LSN]) COLLATE Latin1_General_BIN >= @startLsn");
            parameters.Add(new SqlParameter("@startLsn", SqlDbType.NVarChar, 23) { Value = start.ToString() });
        }
        if (options.EndLsn is Lsn end)
        {
            sb.Append("\n  AND UPPER([Current LSN]) COLLATE Latin1_General_BIN <= @endLsn");
            parameters.Add(new SqlParameter("@endLsn", SqlDbType.NVarChar, 23) { Value = end.ToString() });
        }
        return sb.ToString();
    }

    /// <summary>Positions of the transaction log backups (of the current database) inside the backup media.</summary>
    private static async Task<IReadOnlyList<int>> GetLogBackupPositionsAsync(SqlConnection connection, string[] files, CancellationToken ct)
    {
        try
        {
            var sql = new StringBuilder("RESTORE HEADERONLY FROM ");
            var parameters = new List<SqlParameter>();
            for (int i = 0; i < files.Length; i++)
            {
                if (i > 0) sql.Append(", ");
                sql.Append("DISK = @h").Append(i);
                parameters.Add(new SqlParameter("@h" + i, SqlDbType.NVarChar, 260) { Value = files[i] });
            }
            await using var cmd = new SqlCommand(sql.ToString(), connection);
            cmd.Parameters.AddRange(parameters.ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            int typeOrdinal = reader.GetOrdinal("BackupType");
            int positionOrdinal = reader.GetOrdinal("Position");
            int dbOrdinal = reader.GetOrdinal("DatabaseName");
            var positions = new List<int>();
            var otherDatabases = new List<int>();
            while (await reader.ReadAsync(ct))
            {
                if (Convert.ToInt32(reader.GetValue(typeOrdinal)) != 2) continue; // 2 = transaction log
                int position = Convert.ToInt32(reader.GetValue(positionOrdinal));
                var db = reader.IsDBNull(dbOrdinal) ? null : reader.GetString(dbOrdinal);
                if (string.Equals(db, connection.Database, StringComparison.OrdinalIgnoreCase)) positions.Add(position);
                else otherDatabases.Add(position);
            }
            if (positions.Count > 0) return positions;
            if (otherDatabases.Count > 0) return otherDatabases;
            throw new InvalidOperationException($"'{string.Join(", ", files)}' does not contain a transaction log backup.");
        }
        catch (SqlException)
        {
            return new[] { 1 }; // no permission for RESTORE HEADERONLY: read the first backup set
        }
    }

    private static async Task ReadRecordsAsync(SqlConnection connection, string sql, List<SqlParameter> parameters, string source,
        List<LogRecord> target, LogSourceOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = options.CommandTimeoutSeconds };
        cmd.Parameters.AddRange(parameters.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        int count = 0;
        while (await reader.ReadAsync(ct))
        {
            target.Add(ReadRecord(reader, source));
            if (++count % 50_000 == 0)
                progress?.Report($"Read {count:N0} log records from {source}...");
        }
    }

    private static LogRecord ReadRecord(SqlDataReader r, string source)
    {
        var contents = new byte[]?[5];
        for (int i = 0; i < 5; i++)
            contents[i] = r.IsDBNull(19 + i) ? null : (byte[])r.GetValue(19 + i);

        return new LogRecord
        {
            Lsn = Lsn.Parse(r.GetString(0)),
            PreviousLsn = r.IsDBNull(1) ? Lsn.Zero : Lsn.TryParse(r.GetString(1), out var prev) ? prev : Lsn.Zero,
            Operation = r.GetString(2),
            Context = r.IsDBNull(3) ? null : r.GetString(3),
            TransactionId = r.IsDBNull(4) ? "" : r.GetString(4),
            AllocUnitId = r.IsDBNull(5) ? null : r.GetInt64(5),
            AllocUnitName = r.IsDBNull(6) ? null : r.GetString(6),
            PageId = r.IsDBNull(7) ? null : r.GetString(7),
            SlotId = r.IsDBNull(8) ? null : r.GetInt32(8),
            OffsetInRow = r.IsDBNull(9) ? null : r.GetInt16(9),
            ModifySize = r.IsDBNull(10) ? null : r.GetInt16(10),
            TransactionName = r.IsDBNull(11) ? null : r.GetString(11),
            BeginTime = r.IsDBNull(12) ? null : ParseLogTime(r.GetString(12)),
            EndTime = r.IsDBNull(13) ? null : ParseLogTime(r.GetString(13)),
            TransactionSid = r.IsDBNull(14) ? null : (byte[])r.GetValue(14),
            UserName = r.IsDBNull(15) ? null : r.GetString(15),
            Description = r.IsDBNull(16) ? null : r.GetString(16),
            LockInformation = r.IsDBNull(17) ? null : r.GetString(17),
            RecordBytes = r.IsDBNull(18) ? null : (byte[])r.GetValue(18),
            RowLogContents = contents,
            Source = source,
        };
    }

    /// <summary>Parses the fn_dblog time format, e.g. <c>2026/10/04 10:08:46:507</c>.</summary>
    public static DateTime? ParseLogTime(string text)
    {
        if (DateTime.TryParseExact(text.Trim(), "yyyy/MM/dd HH:mm:ss:fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ? t : null;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
