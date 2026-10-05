namespace SqlLogReader.Log;

/// <summary>
/// One transaction log record, as returned by <c>fn_dblog</c> / <c>fn_dump_dblog</c>
/// or as parsed straight out of an .ldf file.
/// </summary>
public sealed class LogRecord
{
    public Lsn Lsn { get; init; }
    public Lsn PreviousLsn { get; init; }

    /// <summary>Operation name, e.g. <c>LOP_INSERT_ROWS</c>.</summary>
    public string Operation { get; init; } = "";

    /// <summary>Context name, e.g. <c>LCX_CLUSTERED</c>.</summary>
    public string? Context { get; init; }

    /// <summary>Transaction id in fn_dblog form, e.g. <c>0000:0000037f</c>.</summary>
    public string TransactionId { get; init; } = "";

    public long? AllocUnitId { get; init; }
    public string? AllocUnitName { get; init; }

    /// <summary>Page id in fn_dblog form (<c>0001:00000150</c> = file 1, page 0x150).</summary>
    public string? PageId { get; init; }
    public int? SlotId { get; init; }
    public int? OffsetInRow { get; init; }
    public int? ModifySize { get; init; }

    public string? TransactionName { get; init; }
    public DateTime? BeginTime { get; init; }
    public DateTime? EndTime { get; init; }
    public byte[]? TransactionSid { get; init; }
    public string? UserName { get; set; }
    public string? Description { get; init; }
    public string? LockInformation { get; init; }

    /// <summary>The complete binary log record (<c>[Log Record]</c> column).</summary>
    public byte[]? RecordBytes { get; init; }

    /// <summary>
    /// <c>[RowLog Contents 0..4]</c>. Only used as a fallback when <see cref="RecordBytes"/>
    /// cannot be split into its elements.
    /// </summary>
    public byte[]?[] RowLogContents { get; init; } = Array.Empty<byte[]?>();

    /// <summary>Identifies where the record came from (online log, backup file, .ldf file).</summary>
    public string? Source { get; init; }

    public bool IsCompensation =>
        Description != null && Description.Contains("COMPENSATION", StringComparison.OrdinalIgnoreCase);

    private byte[][]? _elements;
    private bool _elementsParsed;

    /// <summary>
    /// The variable-length payload items ("row log contents") of the record. For row operations
    /// these are e.g. the row image for an insert, or the before/after bytes of an update.
    /// </summary>
    public IReadOnlyList<byte[]> Elements
    {
        get
        {
            if (!_elementsParsed)
            {
                _elements = LogRecordPayload.ResolveElements(this);
                _elementsParsed = true;
            }
            return _elements!;
        }
    }

    /// <summary>Decodes <see cref="PageId"/> into file id and page number.</summary>
    public bool TryGetPage(out int fileId, out int pageNumber)
    {
        fileId = 0;
        pageNumber = 0;
        if (string.IsNullOrEmpty(PageId)) return false;
        var parts = PageId.Split(':');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out fileId)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out pageNumber);
    }

    public override string ToString() => $"{Lsn} {Operation} {Context} {AllocUnitName}";
}
