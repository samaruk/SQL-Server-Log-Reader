namespace SqlLogReader.Log;

/// <summary>Where log records are read from.</summary>
public sealed class LogSourceOptions
{
    /// <summary>Read the database's online transaction log (<c>fn_dblog</c>).</summary>
    public bool IncludeOnlineLog { get; set; } = true;

    /// <summary>
    /// Transaction log backup files to read with <c>fn_dump_dblog</c>. Paths are on the SQL Server machine.
    /// A backup striped over several files can be given as one entry with paths separated by '|'.
    /// </summary>
    public IReadOnlyList<string> BackupFiles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Enable trace flag 2537 for the session so that <c>fn_dblog</c> also returns records from the
    /// inactive part of the log that has not been overwritten yet. Requires sysadmin.
    /// </summary>
    public bool IncludeInactiveLog { get; set; }

    /// <summary>Only records with an LSN at or after this one.</summary>
    public Lsn? StartLsn { get; set; }

    /// <summary>Only records with an LSN at or before this one.</summary>
    public Lsn? EndLsn { get; set; }

    /// <summary>Command timeout in seconds (0 = no limit).</summary>
    public int CommandTimeoutSeconds { get; set; }
}
