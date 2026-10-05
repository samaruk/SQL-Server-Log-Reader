using SqlLogReader.Analysis;
using SqlLogReader.Log;
using SqlLogReader.Schema;
using SqlLogReader.Server;

namespace SqlLogReader;

/// <summary>What to read and how to filter it.</summary>
public sealed class LogReadRequest
{
    public required ConnectionSettings Connection { get; init; }
    public required string Database { get; init; }
    public LogSourceOptions Source { get; init; } = new();
    public AnalyzerOptions Analysis { get; init; } = new();
}

public sealed class LogReadResult
{
    public required string Database { get; init; }
    public required DatabaseSchema Schema { get; init; }
    public required IReadOnlyList<LogRecord> Records { get; init; }
    public required LogAnalysisResult Analysis { get; init; }

    public IReadOnlyList<RowChange> Changes => Analysis.Changes;
    public IReadOnlyList<TransactionInfo> Transactions => Analysis.Transactions;
    public IReadOnlyList<string> Warnings => Analysis.Warnings;
}

/// <summary>Reads and decodes the transaction log of a database: the main entry point of the library.</summary>
public static class LogReaderService
{
    public static async Task<LogReadResult> ReadAsync(LogReadRequest request, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await request.Connection.OpenAsync(request.Database, cancellationToken);

        progress?.Report("Loading table metadata...");
        var schema = await SchemaLoader.LoadAsync(connection, cancellationToken);

        var records = await SqlLogSource.ReadAsync(connection, request.Source, progress, cancellationToken);
        progress?.Report($"Analysing {records.Count:N0} log records...");

        var currentRows = request.Analysis.UseCurrentData && request.Source.IncludeOnlineLog ? new SqlPageRowSource(connection) : null;
        var analyzer = new LogAnalyzer(schema, currentRows);
        var analysis = await analyzer.AnalyzeAsync(records, request.Analysis, progress, cancellationToken);

        progress?.Report($"Found {analysis.Changes.Count:N0} row change(s) in {analysis.Transactions.Count:N0} transaction(s).");
        return new LogReadResult { Database = request.Database, Schema = schema, Records = records, Analysis = analysis };
    }

    /// <summary>
    /// Reads an .ldf file directly. Table layouts (and login names) come from <paramref name="schemaDatabase"/>,
    /// a database with the same tables, for example the database itself or an attached copy. Without it,
    /// rows cannot be decoded and only transactions are returned.
    /// </summary>
    /// <param name="paths">The log file(s) of one database.</param>
    public static async Task<LogReadResult> ReadLdfFilesAsync(IReadOnlyList<string> paths, ConnectionSettings? connection, string? schemaDatabase,
        AnalyzerOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var ldf = await Task.Run(() => LdfFileReader.ReadFiles(paths, progress, cancellationToken), cancellationToken);
        progress?.Report($"Read {ldf.Records.Count:N0} log records from {ldf.VirtualLogFiles.Count(v => v.Records > 0)} of " +
                         $"{ldf.VirtualLogFiles.Count} virtual log files.");

        LogAnalysisResult analysis;
        DatabaseSchema schema;
        if (connection != null && !string.IsNullOrWhiteSpace(schemaDatabase))
        {
            await using var sql = await connection.OpenAsync(schemaDatabase, cancellationToken);
            progress?.Report($"Loading table metadata from {schemaDatabase}...");
            schema = await SchemaLoader.LoadAsync(sql, cancellationToken);
            await ServerExplorer.ResolveUserNamesAsync(sql, ldf.Records, cancellationToken);
            var currentRows = options.UseCurrentData ? new SqlPageRowSource(sql) : null;
            analysis = await new LogAnalyzer(schema, currentRows).AnalyzeAsync(ldf.Records, options, progress, cancellationToken);
        }
        else
        {
            schema = new DatabaseSchema("", Array.Empty<TableInfo>());
            analysis = await new LogAnalyzer(schema).AnalyzeAsync(ldf.Records, options, progress, cancellationToken);
        }

        var warnings = ldf.Warnings.Concat(analysis.Warnings).ToList();
        if (schema.Tables.Count == 0)
            warnings.Add("No table metadata: rows were not decoded. Choose a database with the same tables to decode them.");
        var merged = new LogAnalysisResult
        {
            Changes = analysis.Changes,
            Transactions = analysis.Transactions,
            Warnings = warnings,
            RecordCount = analysis.RecordCount,
            FirstLsn = analysis.FirstLsn,
            LastLsn = analysis.LastLsn,
        };
        return new LogReadResult { Database = schemaDatabase ?? Path.GetFileName(paths[0]), Schema = schema, Records = ldf.Records, Analysis = merged };
    }
}
