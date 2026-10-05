namespace SqlLogReader.Analysis;

public enum TransactionState
{
    /// <summary>LOP_COMMIT_XACT found.</summary>
    Committed,
    /// <summary>LOP_ABORT_XACT found: the transaction was rolled back.</summary>
    RolledBack,
    /// <summary>No end record in the log that was read (still open, or the log was cut).</summary>
    Unknown,
}

/// <summary>A transaction seen in the log.</summary>
public sealed class TransactionInfo
{
    public required string TransactionId { get; init; }
    public string? Name { get; set; }
    public DateTime? BeginTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? UserName { get; set; }
    public byte[]? UserSid { get; set; }
    public Lsn? BeginLsn { get; set; }
    public Lsn? EndLsn { get; set; }
    public TransactionState State { get; set; } = TransactionState.Unknown;

    /// <summary>Number of row changes attributed to this transaction.</summary>
    public int ChangeCount { get; set; }

    /// <summary>Tables touched by this transaction's row changes.</summary>
    public SortedSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SID rendered as hex when no login name could be resolved.</summary>
    public string DisplayUser => UserName ?? (UserSid is { Length: > 0 } ? "0x" + Convert.ToHexString(UserSid) : "");

    public override string ToString() => $"{TransactionId} {Name} {BeginTime:yyyy-MM-dd HH:mm:ss} {State}";
}
