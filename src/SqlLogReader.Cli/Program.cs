using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlLogReader;
using SqlLogReader.Analysis;
using SqlLogReader.Cli;
using SqlLogReader.Export;
using SqlLogReader.Log;
using SqlLogReader.Schema;
using SqlLogReader.Scripting;
using SqlLogReader.Server;

Console.OutputEncoding = Encoding.UTF8;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

CommandLine cl;
try
{
    cl = CommandLine.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (cl.Command is "" or "help" || cl.Has("--help"))
{
    Console.WriteLine(Help.Text);
    return cl.Command is "" && !cl.Has("--help") ? 2 : 0;
}

try
{
    int code = cl.Command switch
    {
        "databases" => await Commands.DatabasesAsync(cl, cts.Token),
        "read" => await Commands.ReadAsync(cl, cts.Token),
        "transactions" => await Commands.TransactionsAsync(cl, cts.Token),
        "backups" => await Commands.BackupsAsync(cl, cts.Token),
        "vlfs" => await Commands.VlfsAsync(cl, cts.Token),
        "raw" => await Commands.RawAsync(cl, cts.Token),
        "attach" => await Commands.AttachAsync(cl, cts.Token),
        "ldf" => await Commands.LdfAsync(cl, cts.Token),
        _ => throw new ArgumentException($"Unknown command '{cl.Command}'. Run 'sqllogreader help'."),
    };
    foreach (var unused in cl.Unused)
        Console.Error.WriteLine($"warning: option {unused} is not used by '{cl.Command}'.");
    return code;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 2;
}
catch (SqlException ex)
{
    Console.Error.WriteLine($"SQL Server error {ex.Number}: {ex.Message}");
    if (ex.Number is 229 or 297 or 300 or 262 or 8189)
        Console.Error.WriteLine("Reading the transaction log requires membership in the sysadmin server role.");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

internal static class Commands
{
    public static ConnectionSettings Connection(CommandLine cl)
    {
        var settings = new ConnectionSettings
        {
            ConnectionString = cl.Get("--connection-string"),
            Server = cl.Get("--server") ?? "localhost",
            TrustServerCertificate = !cl.Has("--no-trust-cert"),
            Encrypt = cl.Has("--encrypt"),
        };
        var user = cl.Get("--user");
        if (user != null && !cl.Has("--integrated"))
        {
            settings.IntegratedSecurity = false;
            settings.UserName = user;
            settings.Password = cl.Get("--password") ?? Environment.GetEnvironmentVariable("SQLLOGREADER_PASSWORD")
                ?? throw new ArgumentException("--password (or the SQLLOGREADER_PASSWORD environment variable) is required with --user.");
        }
        return settings;
    }

    private static string RequireDatabase(CommandLine cl) =>
        cl.Get("--database") ?? throw new ArgumentException("--database is required.");

    public static async Task<int> DatabasesAsync(CommandLine cl, CancellationToken ct)
    {
        await using var connection = await Connection(cl).OpenAsync("master", ct);
        var server = await ServerExplorer.GetServerSummaryAsync(connection, ct);
        Console.WriteLine($"{server.ServerName}  SQL Server {server.Version} {server.Edition}  sysadmin: {(server.IsSysAdmin ? "yes" : "NO (log reading needs sysadmin)")}");
        Console.WriteLine();
        var dbs = await ServerExplorer.ListDatabasesAsync(connection, ct);
        Console.Write(TextTable.Render(
            new[] { "Database", "Id", "Recovery", "State", "Log reuse wait", "Log MB", "Log used %" },
            dbs.Select(d => new[] { d.Name, d.DatabaseId.ToString(CultureInfo.InvariantCulture), d.RecoveryModel, d.State, d.LogReuseWait,
                d.LogSizeMb.ToString("0.00", CultureInfo.InvariantCulture), d.LogUsedPercent.ToString("0.0", CultureInfo.InvariantCulture) })));
        return 0;
    }

    private static LogReadRequest BuildRequest(CommandLine cl)
    {
        var source = new LogSourceOptions
        {
            IncludeOnlineLog = !cl.Has("--no-online-log"),
            BackupFiles = cl.GetAll("--backup", splitCommas: false),
            IncludeInactiveLog = cl.Has("--include-inactive"),
            CommandTimeoutSeconds = cl.GetInt("--timeout", 0),
            StartLsn = ParseLsn(cl.Get("--start-lsn"), "--start-lsn"),
            EndLsn = ParseLsn(cl.Get("--end-lsn"), "--end-lsn"),
        };
        if (!source.IncludeOnlineLog && source.BackupFiles.Count == 0)
            throw new ArgumentException("--no-online-log needs at least one --backup file.");

        return new LogReadRequest { Connection = Connection(cl), Database = RequireDatabase(cl), Source = source, Analysis = AnalyzerOptionsFrom(cl) };
    }

    private static AnalyzerOptions AnalyzerOptionsFrom(CommandLine cl)
    {
        return new AnalyzerOptions
        {
            From = ParseTime(cl.Get("--from"), "--from"),
            To = ParseTime(cl.Get("--to"), "--to"),
            Tables = cl.GetAll("--table"),
            Operations = cl.GetAll("--op").Select(ParseOperation).ToArray(),
            UserName = cl.Get("--login"),
            TransactionId = cl.Get("--transaction"),
            IncludeRolledBack = cl.Has("--include-rolled-back"),
            IncludeAllTransactions = cl.Has("--all-transactions"),
            UseCurrentData = !cl.Has("--no-current-data"),
        };
    }

    private static async Task<LogReadResult> ReadLogAsync(CommandLine cl, CancellationToken ct)
    {
        var request = BuildRequest(cl);
        var result = await LogReaderService.ReadAsync(request, new SyncProgress(m => Console.Error.WriteLine(m)), ct);
        foreach (var warning in result.Warnings)
            Console.Error.WriteLine("warning: " + warning);
        return result;
    }

    public static async Task<int> ReadAsync(CommandLine cl, CancellationToken ct)
    {
        var result = await ReadLogAsync(cl, ct);
        return await WriteChangesAsync(cl, result.Changes, result.Database, ct);
    }

    private static async Task<int> WriteChangesAsync(CommandLine cl, IReadOnlyList<RowChange> changes, string? database, CancellationToken ct)
    {
        var format = (cl.Get("--format") ?? "table").ToLowerInvariant();
        var output = cl.Get("--output");

        string text;
        switch (format)
        {
            case "table":
                text = TextTable.Render(
                    new[] { "LSN", "Time", "User", "Operation", "Table", "Key", "Changes" },
                    changes.Select(c => new[]
                    {
                        c.Lsn.ToString(), c.Time?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                        c.UserName, c.Operation.ToString().ToUpperInvariant() + (c.IsComplete ? "" : "*"), c.TableName, c.KeyText, c.Summary,
                    }),
                    new[] { 0, 0, 24, 0, 40, 40, 120 });
                if (changes.Any(c => !c.IsComplete))
                    text += "* only part of the row could be reconstructed (see --format details)" + Environment.NewLine;
                break;
            case "details":
                text = Details(changes);
                break;
            case "csv":
                using (var sw = new StringWriter(CultureInfo.InvariantCulture))
                {
                    ChangeExporter.WriteCsv(sw, changes);
                    text = sw.ToString();
                }
                break;
            case "json":
                using (var ms = new MemoryStream())
                {
                    ChangeExporter.WriteJson(ms, changes);
                    text = Encoding.UTF8.GetString(ms.ToArray());
                }
                break;
            case "undo":
                text = ScriptGenerator.GenerateScript(changes, ScriptKind.Undo, database);
                break;
            case "redo":
                text = ScriptGenerator.GenerateScript(changes, ScriptKind.Redo, database);
                break;
            default:
                throw new ArgumentException("--format must be table, details, csv, json, undo or redo.");
        }

        if (output != null)
        {
            await File.WriteAllTextAsync(output, text, new UTF8Encoding(format == "csv"), ct);
            Console.Error.WriteLine($"Wrote {changes.Count} change(s) to {output}.");
        }
        else
        {
            Console.Write(text);
            if (format is "table") Console.WriteLine($"{changes.Count} change(s).");
        }
        return 0;
    }

    private static string Details(IReadOnlyList<RowChange> changes)
    {
        var sb = new StringBuilder();
        foreach (var c in changes)
        {
            sb.AppendLine($"=== {c.Operation.ToString().ToUpperInvariant()} {c.TableName}   LSN {c.Lsn}   page {c.Record.PageId} slot {c.Record.SlotId}");
            sb.AppendLine($"    transaction {c.Transaction.TransactionId} '{c.Transaction.Name}'  begin {c.Transaction.BeginTime:yyyy-MM-dd HH:mm:ss.fff}  " +
                          $"end {c.Transaction.EndTime:yyyy-MM-dd HH:mm:ss.fff}  user {c.UserName}  {c.Transaction.State}");
            if (!string.IsNullOrEmpty(c.KeyText)) sb.AppendLine($"    key: {c.KeyText}");
            foreach (var note in c.Notes) sb.AppendLine($"    note: {note}");
            var rows = c.Columns.Select(col => new[]
            {
                (col.IsChanged && c.Operation == ChangeOperation.Update ? "* " : "  ") + col.Name,
                col.Column.FullTypeName,
                col.Before?.DisplayText ?? "",
                col.After?.DisplayText ?? "",
            });
            var table = TextTable.Render(new[] { "  Column", "Type", "Old value", "New value" }, rows, new[] { 0, 0, 60, 60 });
            foreach (var line in table.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                sb.AppendLine("    " + line);
            sb.AppendLine();
        }
        sb.AppendLine($"{changes.Count} change(s).");
        return sb.ToString();
    }

    public static async Task<int> TransactionsAsync(CommandLine cl, CancellationToken ct)
    {
        var result = await ReadLogAsync(cl, ct);
        PrintTransactions(result.Transactions);
        return 0;
    }

    private static void PrintTransactions(IReadOnlyList<TransactionInfo> transactions)
    {
        Console.Write(TextTable.Render(
            new[] { "Transaction", "Name", "Begin", "End", "User", "State", "Rows", "Tables" },
            transactions.Select(t => new[]
            {
                t.TransactionId, t.Name ?? "", t.BeginTime?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "",
                t.EndTime?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "", t.DisplayUser, t.State.ToString(),
                t.ChangeCount.ToString(CultureInfo.InvariantCulture), string.Join(", ", t.Tables),
            }),
            new[] { 0, 33, 0, 0, 30, 0, 0, 80 }));
        Console.WriteLine($"{transactions.Count} transaction(s).");
    }

    /// <summary>Reads an .ldf file directly (no SQL Server needed to read it; table metadata comes from --database).</summary>
    public static async Task<int> LdfAsync(CommandLine cl, CancellationToken ct)
    {
        var files = cl.GetAll("--file", splitCommas: false).Concat(cl.Positional).ToList();
        if (files.Count == 0) throw new ArgumentException("--file <path to the .ldf file> is required.");
        var progress = new SyncProgress(m => Console.Error.WriteLine(m));
        var ldf = LdfFileReader.ReadFiles(files, progress, ct);
        foreach (var warning in ldf.Warnings) Console.Error.WriteLine("warning: " + warning);
        Console.Error.WriteLine($"Read {ldf.Records.Count:N0} log records from {ldf.VirtualLogFiles.Count(v => v.Records > 0)} " +
                                $"of {ldf.VirtualLogFiles.Count} virtual log files.");

        var start = ParseLsn(cl.Get("--start-lsn"), "--start-lsn");
        var end = ParseLsn(cl.Get("--end-lsn"), "--end-lsn");
        var records = ldf.Records.Where(r => (start == null || r.Lsn >= start) && (end == null || r.Lsn <= end)).ToList();

        if (cl.Has("--vlfs"))
        {
            Console.Write(TextTable.Render(
                new[] { "Offset", "Size", "Sequence", "Parity", "Blocks", "Records" },
                ldf.VirtualLogFiles.Select(v => new[]
                {
                    v.Offset.ToString(CultureInfo.InvariantCulture), v.Size.ToString(CultureInfo.InvariantCulture),
                    v.Sequence.ToString(CultureInfo.InvariantCulture), $"0x{v.Parity:X2}",
                    v.Blocks.ToString(CultureInfo.InvariantCulture), v.Records.ToString(CultureInfo.InvariantCulture),
                })));
            return 0;
        }
        if (cl.Has("--raw"))
        {
            int top = cl.GetInt("--top", 1000);
            PrintRawRecords(top > 0 ? records.Take(top).ToList() : records);
            return 0;
        }

        var analysis = AnalyzerOptionsFrom(cl);
        var database = cl.Get("--database");
        if (database == null)
        {
            Console.Error.WriteLine("No --database given, so table layouts are unknown and rows cannot be decoded. Listing transactions;");
            Console.Error.WriteLine("pass -S <server> -d <database with the same tables> (for example the database itself or an attached copy) to decode rows.");
            analysis.IncludeAllTransactions = true;
            var bare = await new LogAnalyzer(new DatabaseSchema("", Array.Empty<TableInfo>())).AnalyzeAsync(records, analysis, progress, ct);
            // Allocation units of user objects have an index id of 256 or more (ids from 2^56 up); lower ids are system tables.
            var rowOps = records.Where(r => LogAnalyzer.RowOperations.Contains(r.Operation) && r.Context != null
                                            && LogAnalyzer.DataContexts.Contains(r.Context) && r.AllocUnitId >= 1L << 56)
                .GroupBy(r => r.TransactionId).ToDictionary(g => g.Key, g => g.ToList());
            var interesting = bare.Transactions.Where(t => rowOps.ContainsKey(t.TransactionId) || LogAnalyzer.IsDdlTransaction(t)).ToList();
            Console.Write(TextTable.Render(
                new[] { "Transaction", "Name", "Begin", "End", "User SID", "State", "Row operations" },
                interesting.Select(t => new[]
                {
                    t.TransactionId, t.Name ?? "", t.BeginTime?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "",
                    t.EndTime?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "", t.DisplayUser, t.State.ToString(),
                    rowOps.TryGetValue(t.TransactionId, out var ops)
                        ? string.Join(", ", ops.GroupBy(o => (o.Operation, o.AllocUnitId)).Select(g => $"{g.Count()}x {g.Key.Operation[4..]} on allocation unit {g.Key.AllocUnitId}"))
                        : "",
                }),
                new[] { 0, 33, 0, 0, 0, 0, 120 }));
            Console.WriteLine($"{interesting.Count} transaction(s).");
            return 0;
        }

        await using var connection = await Connection(cl).OpenAsync(database, ct);
        var schema = await SchemaLoader.LoadAsync(connection, ct);
        await ServerExplorer.ResolveUserNamesAsync(connection, records, ct);
        var currentRows = analysis.UseCurrentData ? new SqlPageRowSource(connection) : null;
        var result = await new LogAnalyzer(schema, currentRows).AnalyzeAsync(records, analysis, progress, ct);
        foreach (var warning in result.Warnings) Console.Error.WriteLine("warning: " + warning);
        if (cl.Has("--transactions"))
        {
            PrintTransactions(result.Transactions);
            return 0;
        }
        return await WriteChangesAsync(cl, result.Changes, database, ct);
    }

    public static async Task<int> BackupsAsync(CommandLine cl, CancellationToken ct)
    {
        var db = RequireDatabase(cl);
        await using var connection = await Connection(cl).OpenAsync("master", ct);
        var backups = await ServerExplorer.GetLogBackupHistoryAsync(connection, db, ct);
        Console.Write(TextTable.Render(
            new[] { "Started", "Finished", "First LSN", "Last LSN", "Position", "File" },
            backups.Select(b => new[]
            {
                b.StartTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), b.FinishTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                b.FirstLsn, b.LastLsn, b.Position.ToString(CultureInfo.InvariantCulture), b.PhysicalDeviceName,
            })));
        Console.WriteLine($"{backups.Count} log backup(s). Read them with: sqllogreader read -d \"{db}\" --backup <file>");
        return 0;
    }

    public static async Task<int> VlfsAsync(CommandLine cl, CancellationToken ct)
    {
        await using var connection = await Connection(cl).OpenAsync(RequireDatabase(cl), ct);
        var vlfs = await ServerExplorer.GetVirtualLogFilesAsync(connection, ct);
        Console.Write(TextTable.Render(
            new[] { "Offset", "Size MB", "Sequence", "Active", "Status", "Parity", "First LSN" },
            vlfs.Select(v => new[]
            {
                v.BeginOffset.ToString(CultureInfo.InvariantCulture), v.SizeMb.ToString("0.00", CultureInfo.InvariantCulture),
                v.SequenceNumber.ToString(CultureInfo.InvariantCulture), v.IsActive ? "yes" : "no",
                v.Status.ToString(CultureInfo.InvariantCulture), v.Parity.ToString(CultureInfo.InvariantCulture), v.FirstLsn,
            })));
        return 0;
    }

    public static async Task<int> RawAsync(CommandLine cl, CancellationToken ct)
    {
        await using var connection = await Connection(cl).OpenAsync(RequireDatabase(cl), ct);
        var options = new LogSourceOptions
        {
            StartLsn = ParseLsn(cl.Get("--start-lsn"), "--start-lsn"),
            EndLsn = ParseLsn(cl.Get("--end-lsn"), "--end-lsn"),
            CommandTimeoutSeconds = cl.GetInt("--timeout", 0),
        };
        var records = await SqlLogSource.ReadRawAsync(connection, cl.GetInt("--top", 1000), options, ct);
        PrintRawRecords(records);
        return 0;
    }

    private static void PrintRawRecords(IReadOnlyList<LogRecord> records)
    {
        Console.Write(TextTable.Render(
            new[] { "LSN", "Operation", "Context", "Transaction", "Object", "Page", "Slot", "Description" },
            records.Select(r => new[]
            {
                r.Lsn.ToString(), r.Operation, r.Context ?? "", r.TransactionId,
                r.AllocUnitName ?? (r.AllocUnitId is long au ? au.ToString(CultureInfo.InvariantCulture) : ""), r.PageId ?? "",
                r.SlotId?.ToString(CultureInfo.InvariantCulture) ?? "",
                r.Description ?? (r.TransactionName != null ? $"{r.TransactionName} {r.BeginTime:yyyy-MM-dd HH:mm:ss.fff}" : r.EndTime?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? ""),
            }),
            new[] { 0, 0, 0, 0, 50, 0, 0, 80 }));
        Console.WriteLine($"{records.Count} record(s).");
    }

    public static async Task<int> AttachAsync(CommandLine cl, CancellationToken ct)
    {
        var name = RequireDatabase(cl);
        var mdf = cl.Get("--mdf") ?? throw new ArgumentException("--mdf <path to .mdf on the server> is required.");
        var ldf = cl.GetAll("--ldf", splitCommas: false);
        if (ldf.Count == 0) throw new ArgumentException("--ldf <path to .ldf on the server> is required (the log is what we want to read).");
        await using var connection = await Connection(cl).OpenAsync("master", ct);
        await ServerExplorer.AttachDatabaseAsync(connection, name, mdf, ldf, ct);
        Console.WriteLine($"Attached '{name}'. Read its log with: sqllogreader read -d \"{name}\"");
        return 0;
    }

    private static Lsn? ParseLsn(string? text, string option)
    {
        if (text == null) return null;
        if (Lsn.TryParseAny(text, out var lsn)) return lsn;
        throw new ArgumentException($"{option}: '{text}' is not an LSN (use 0000002A:00000168:0001 or 42000000036000001).");
    }

    private static DateTime? ParseTime(string? text, string option)
    {
        if (text == null) return null;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var t)) return t;
        throw new ArgumentException($"{option}: '{text}' is not a date/time (use e.g. 2026-10-04 or \"2026-10-04 13:30\").");
    }

    private static ChangeOperation ParseOperation(string text) => text.ToLowerInvariant() switch
    {
        "insert" or "i" => ChangeOperation.Insert,
        "update" or "u" => ChangeOperation.Update,
        "delete" or "d" => ChangeOperation.Delete,
        _ => throw new ArgumentException($"--op: unknown operation '{text}' (insert, update, delete)."),
    };
}

/// <summary>Reports progress synchronously (Progress&lt;T&gt; posts to the thread pool, which reorders console output).</summary>
internal sealed class SyncProgress : IProgress<string>
{
    private readonly Action<string> _report;
    public SyncProgress(Action<string> report) => _report = report;
    public void Report(string value) => _report(value);
}
