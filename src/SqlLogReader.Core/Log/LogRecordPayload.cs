namespace SqlLogReader.Log;

/// <summary>
/// Splits a binary log record into its variable-length elements.
/// </summary>
/// <remarks>
/// Layout of a row-operation log record (all values little endian):
/// <code>
///  0  ushort  (reserved)
///  2  ushort  length of the fixed part (F)
///  4  10 B    previous LSN in the same transaction
/// 14  ushort  flags
/// 16  6 B     transaction id
/// 22  byte    operation      23 byte context
/// 24  6 B     page id (4 B page, 2 B file)       30 ushort slot
/// 32  uint    allocation unit object id (m_objId)
/// 36  10 B    previous page LSN
/// 46  ushort  allocation unit index id (m_indexId); allocation unit id = (index id &lt;&lt; 48) | (object id &lt;&lt; 16)
/// 48  8 B     partition (HoBt) id
/// 56  ushort  offset in row  58 ushort modify size
///  F  ushort  number of elements (n), followed by n ushort lengths;
///             then the elements, each starting on a 4-byte boundary.
/// </code>
/// </remarks>
public static class LogRecordPayload
{
    /// <summary>Header offsets used when parsing raw records read from .ldf files.</summary>
    public const int OperationOffset = 22;
    public const int ContextOffset = 23;
    public const int TransactionIdOffset = 16;
    public const int PageIdOffset = 24;
    public const int SlotIdOffset = 30;
    public const int AllocUnitObjectIdOffset = 32;
    public const int AllocUnitIndexIdOffset = 46;
    public const int PartitionIdOffset = 48;
    public const int OffsetInRowOffset = 56;
    public const int ModifySizeOffset = 58;

    private const int MaxElements = 256;

    /// <summary>Splits <paramref name="record"/> into elements. Returns false when the layout does not fit.</summary>
    public static bool TryParseElements(ReadOnlySpan<byte> record, out byte[][] elements)
    {
        elements = Array.Empty<byte[]>();
        if (record.Length < 6) return false;

        int fixedLength = BitConverter.ToUInt16(record.Slice(2, 2));
        if (fixedLength < 24 || fixedLength + 2 > record.Length) return false;

        int count = BitConverter.ToUInt16(record.Slice(fixedLength, 2));
        if (count == 0 || count > MaxElements) return false;

        int pos = fixedLength + 2;
        if (pos + count * 2 > record.Length) return false;

        var lengths = new int[count];
        for (int i = 0; i < count; i++)
            lengths[i] = BitConverter.ToUInt16(record.Slice(pos + i * 2, 2));
        pos = Align4(pos + count * 2);

        var result = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            if (pos + lengths[i] > record.Length) return false;
            result[i] = record.Slice(pos, lengths[i]).ToArray();
            pos = Align4(pos + lengths[i]);
        }

        elements = result;
        return true;
    }

    /// <summary>
    /// Returns the elements for <paramref name="record"/>: parsed from the full binary record when
    /// possible (and consistent with <c>[RowLog Contents 0]</c>), otherwise the RowLog Contents columns.
    /// </summary>
    public static byte[][] ResolveElements(LogRecord record)
    {
        if (record.RecordBytes is { Length: > 0 } bytes && TryParseElements(bytes, out var parsed))
        {
            var rc0 = record.RowLogContents.Length > 0 ? record.RowLogContents[0] : null;
            if (rc0 == null || rc0.Length == 0 || parsed[0].AsSpan().SequenceEqual(rc0))
                return parsed;
        }

        // Fallback: fn_dblog's RowLog Contents columns (up to five of them).
        var list = new List<byte[]>();
        foreach (var item in record.RowLogContents)
            list.Add(item ?? Array.Empty<byte>());
        while (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        return list.ToArray();
    }

    internal static int Align4(int value) => (value + 3) & ~3;
}
