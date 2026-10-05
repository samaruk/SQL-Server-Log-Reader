using SqlLogReader.Schema;

namespace SqlLogReader.Decoding;

/// <summary>A decoded row image.</summary>
public sealed class DecodedRow
{
    public required IReadOnlyList<ColumnValue> Values { get; init; }

    /// <summary>The uniqueifier of a non-unique clustered index row (0 when absent).</summary>
    public int Uniqueifier { get; init; }

    public ColumnValue? this[string columnName] =>
        Values.FirstOrDefault(v => string.Equals(v.Name, columnName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Decodes FixedVar data records into column values using the table's physical layout.</summary>
public static class RowDecoder
{
    /// <summary>Decodes a full data row. Returns false (with a reason) when the bytes are not a decodable row.</summary>
    public static bool TryDecode(TableInfo table, byte[] row, out DecodedRow decoded, out string? error)
    {
        decoded = null!;
        error = null;
        if (table.IsCompressed)
        {
            error = "row/page compressed tables use the CD record format, which is not supported";
            return false;
        }
        if (!DataRecord.TryParseData(row, out var record))
        {
            error = "the bytes are not a valid data record";
            return false;
        }

        var values = new List<ColumnValue>(table.UserColumns.Count);
        foreach (var column in table.UserColumns)
            values.Add(DecodeColumn(record, column, column.LeafOffset, column.LeafNullBit, column.LeafBitPosition));

        int uniqueifier = 0;
        var uniq = table.Columns.FirstOrDefault(c => c.IsUniqueifier);
        if (uniq != null)
            uniqueifier = ReadUniqueifier(record, uniq.LeafOffset);

        decoded = new DecodedRow { Values = values, Uniqueifier = uniqueifier };
        return true;
    }

    /// <summary>
    /// Decodes the clustered key carried by update records (an index-format record).
    /// Returns the key columns ordered by key position, excluding the uniqueifier.
    /// </summary>
    public static bool TryDecodeKey(TableInfo table, byte[] keyRecord, out DecodedRow decoded)
    {
        decoded = null!;
        var keyColumns = table.ClusteredKeyColumns;
        if (keyColumns.Count == 0 || keyRecord.Length < 1 || table.IsCompressed) return false;
        var type = (RecordType)((keyRecord[0] >> 1) & 0x07);
        if (type != RecordType.Index && type != RecordType.GhostIndex) return false;

        int fixedEnd = 1;
        foreach (var c in keyColumns.Where(c => c.InternalOffset > 0))
            fixedEnd = Math.Max(fixedEnd, c.InternalOffset + ValueDecoder.FixedStorageSize(c));

        if (!DataRecord.TryParseIndex(keyRecord, fixedEnd, out var record)) return false;

        var values = new List<ColumnValue>();
        int uniqueifier = 0;
        foreach (var column in keyColumns)
        {
            if (column.IsUniqueifier)
            {
                uniqueifier = ReadUniqueifier(record, column.InternalOffset);
                continue;
            }
            var value = DecodeColumn(record, column, column.InternalOffset, column.InternalNullBit, column.InternalBitPosition);
            if (value.State == ValueState.Error) return false;
            values.Add(value);
        }

        decoded = new DecodedRow { Values = values, Uniqueifier = uniqueifier };
        return true;
    }

    private static int ReadUniqueifier(DataRecord record, short offset)
    {
        if (offset >= 0) return 0;
        if (record.TryGetVariable(-offset, out var bytes, out _) && bytes.Length >= 4)
            return BitConverter.ToInt32(bytes[..4]);
        return 0;
    }

    private static ColumnValue DecodeColumn(DataRecord record, ColumnInfo column, short offset, int nullBit, byte bitPosition)
    {
        // Column added to the table after this row was written: the row does not contain it.
        if (record.HasNullBitmap && nullBit > record.ColumnCount)
        {
            if (column.DefaultValue != null)
                return new ColumnValue { Column = column, State = ValueState.Value, Value = column.DefaultValue };
            return ColumnValue.Null(column);
        }

        if (nullBit > 0 && record.IsNull(nullBit))
            return ColumnValue.Null(column);

        if (column.IsSparse)
            return ColumnValue.Unavailable(column, "sparse column");

        if (column.IsComputed && offset == 0)
            return ColumnValue.Unavailable(column, "computed column (not stored)");

        try
        {
            if (offset > 0)
            {
                if (column.SystemType == SqlSystemType.Bit)
                {
                    if (!record.TryGetFixed(offset, 1, out var b))
                        return Error(column, "bit outside the fixed-length data");
                    bool bit = ((b[0] >> bitPosition) & 1) != 0;
                    return new ColumnValue { Column = column, State = ValueState.Value, Value = bit, RawBytes = new[] { (byte)(bit ? 1 : 0) } };
                }

                int size = ValueDecoder.FixedStorageSize(column);
                if (!record.TryGetFixed(offset, size, out var fixedBytes))
                    return Error(column, "value outside the fixed-length data");
                return Decoded(column, fixedBytes);
            }

            if (offset < 0)
            {
                int index = -offset;
                if (!record.TryGetVariable(index, out var varBytes, out bool isComplex))
                {
                    // Trailing variable-length columns that are NULL or empty are not stored; the null bit
                    // (checked above) tells them apart.
                    if (index > record.VariableColumnCount)
                        return record.HasNullBitmap ? Decoded(column, ReadOnlySpan<byte>.Empty) : ColumnValue.Null(column);
                    return Error(column, "invalid variable column offset");
                }

                if (isComplex)
                    return ColumnValue.Unavailable(column, "value stored off-row (LOB / row-overflow)", varBytes.ToArray());

                if (column.SystemType is SqlSystemType.Text or SqlSystemType.NText or SqlSystemType.Image)
                    return ColumnValue.Unavailable(column, "text pointer (LOB stored off-row)", varBytes.ToArray());

                return Decoded(column, varBytes);
            }

            return Error(column, "column has no storage location");
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or ArgumentException or OverflowException)
        {
            return Error(column, ex.Message);
        }
    }

    private static ColumnValue Decoded(ColumnInfo column, ReadOnlySpan<byte> bytes)
    {
        var raw = bytes.ToArray();
        if (column.SystemType == SqlSystemType.Xml)
            return ColumnValue.Unavailable(column, "xml (binary XML format)", raw);
        var value = ValueDecoder.Decode(column.SystemType, raw, column.Precision, column.Scale, column.CodePage, column.MaxLength);
        return new ColumnValue { Column = column, State = ValueState.Value, Value = value, RawBytes = raw };
    }

    private static ColumnValue Error(ColumnInfo column, string message) =>
        new() { Column = column, State = ValueState.Error, Note = message };
}
