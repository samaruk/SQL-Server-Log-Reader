namespace SqlLogReader.Analysis;

/// <summary>Filters and behaviour switches for <see cref="LogAnalyzer"/>.</summary>
public sealed class AnalyzerOptions
{
    /// <summary>Only changes of transactions that started at or after this time (server local time).</summary>
    public DateTime? From { get; set; }

    /// <summary>Only changes of transactions that started at or before this time (server local time).</summary>
    public DateTime? To { get; set; }

    /// <summary>Table names ("Table" or "schema.Table"); empty = all tables.</summary>
    public IReadOnlyCollection<string> Tables { get; set; } = Array.Empty<string>();

    /// <summary>Operations to report; empty = all.</summary>
    public IReadOnlyCollection<ChangeOperation> Operations { get; set; } = Array.Empty<ChangeOperation>();

    /// <summary>Only transactions started by this login (case-insensitive substring match).</summary>
    public string? UserName { get; set; }

    /// <summary>Only this transaction id (fn_dblog form, e.g. 0000:0000037f).</summary>
    public string? TransactionId { get; set; }

    /// <summary>Also report changes of rolled-back transactions (and changes undone by a rollback to a savepoint).</summary>
    public bool IncludeRolledBack { get; set; }

    /// <summary>
    /// List every transaction, including internal system transactions. By default only transactions
    /// with row changes and schema changes (CREATE/ALTER/DROP/TRUNCATE...) are listed.
    /// </summary>
    public bool IncludeAllTransactions { get; set; }

    /// <summary>
    /// Read the current row from the database (DBCC PAGE) to rebuild updates whose earlier
    /// history is no longer in the log. Requires sysadmin.
    /// </summary>
    public bool UseCurrentData { get; set; } = true;

    /// <summary>Upper bound for current-row lookups, to keep huge logs from issuing too many queries.</summary>
    public int MaxCurrentRowLookups { get; set; } = 20_000;
}
