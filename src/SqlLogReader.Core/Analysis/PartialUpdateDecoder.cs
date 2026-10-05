using SqlLogReader.Decoding;
using SqlLogReader.Schema;

namespace SqlLogReader.Analysis;

/// <summary>
/// Best-effort decoding of an update when neither full row image is known: fixed-length columns
/// whose bytes lie inside a changed range can be decoded directly from the patch. Assumes the row
/// contains every physical column of the current table definition.
/// </summary>
internal static class PartialUpdateDecoder
{
    private const string NotInLog = "value not contained in the log record";

    public static IReadOnlyList<ColumnChange> Decode(TableInfo table, RowPatch patch)
    {
        int fixedEnd = 4;
        foreach (var c in table.Columns.Where(c => c.LeafOffset > 0))
            fixedEnd = Math.Max(fixedEnd, c.LeafOffset + ValueDecoder.FixedStorageSize(c));
        int columnCount = table.Columns.Count == 0 ? 0 : table.Columns.Max(c => c.LeafNullBit);
        int nullBitmapOffset = fixedEnd + 2;

        var result = new List<ColumnChange>();
        foreach (var column in table.UserColumns)
        {
            bool? nullBefore = ReadNullBit(patch, nullBitmapOffset, column.LeafNullBit, before: true);
            bool? nullAfter = ReadNullBit(patch, nullBitmapOffset, column.LeafNullBit, before: false);
            bool nullFlipped = nullBefore.HasValue && nullAfter.HasValue && nullBefore != nullAfter;

            if (column.LeafOffset > 0 && column.LeafNullBit <= columnCount)
            {
                int offset = column.LeafOffset;
                int size = ValueDecoder.FixedStorageSize(column);
                var range = patch.Ranges.FirstOrDefault(r =>
                    r.Before != null && r.Offset <= offset && offset + size <= r.Offset + Math.Min(r.BeforeLength, r.After.Length));

                if (range != null)
                {
                    var beforeBytes = range.Before!.AsSpan(offset - range.Offset, size);
                    var afterBytes = range.After.AsSpan(offset - range.Offset, size);
                    bool bytesChanged = column.SystemType == SqlSystemType.Bit
                        ? ((beforeBytes[0] >> column.LeafBitPosition) & 1) != ((afterBytes[0] >> column.LeafBitPosition) & 1)
                        : !beforeBytes.SequenceEqual(afterBytes);
                    if (!bytesChanged && !nullFlipped) continue;

                    var before = nullBefore == true ? ColumnValue.Null(column) : DecodeFixed(column, beforeBytes);
                    var after = nullAfter == true ? ColumnValue.Null(column) : DecodeFixed(column, afterBytes);
                    result.Add(new ColumnChange { Column = column, Before = before, After = after, IsChanged = !before.SameAs(after) });
                    continue;
                }

                bool overlaps = patch.Ranges.Any(r => r.Offset < offset + size && offset < r.Offset + Math.Max(r.BeforeLength, r.After.Length));
                if (overlaps || nullFlipped)
                {
                    result.Add(new ColumnChange
                    {
                        Column = column,
                        Before = nullBefore == true ? ColumnValue.Null(column) : ColumnValue.Unavailable(column, NotInLog),
                        After = nullAfter == true ? ColumnValue.Null(column) : ColumnValue.Unavailable(column, NotInLog),
                        IsChanged = true,
                    });
                }
                continue;
            }

            if (nullFlipped)
            {
                result.Add(new ColumnChange
                {
                    Column = column,
                    Before = nullBefore == true ? ColumnValue.Null(column) : ColumnValue.Unavailable(column, NotInLog),
                    After = nullAfter == true ? ColumnValue.Null(column) : ColumnValue.Unavailable(column, NotInLog),
                    IsChanged = true,
                });
            }
        }
        return result;
    }

    private static bool? ReadNullBit(RowPatch patch, int nullBitmapOffset, int nullBit, bool before)
    {
        if (nullBit <= 0) return null;
        int byteOffset = nullBitmapOffset + (nullBit - 1) / 8;
        foreach (var r in patch.Ranges)
        {
            var bytes = before ? r.Before : r.After;
            if (bytes == null) continue;
            // Only trust positions that are not shifted by a length change in the same range.
            int usable = Math.Min(r.BeforeLength, r.After.Length);
            if (byteOffset >= r.Offset && byteOffset < r.Offset + usable)
                return (bytes[byteOffset - r.Offset] & (1 << ((nullBit - 1) % 8))) != 0;
        }
        return null;
    }

    private static ColumnValue DecodeFixed(ColumnInfo column, ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (column.SystemType == SqlSystemType.Bit)
            {
                bool bit = ((bytes[0] >> column.LeafBitPosition) & 1) != 0;
                return new ColumnValue { Column = column, State = ValueState.Value, Value = bit, RawBytes = new[] { (byte)(bit ? 1 : 0) } };
            }
            var raw = bytes.ToArray();
            var value = ValueDecoder.Decode(column.SystemType, raw, column.Precision, column.Scale, column.CodePage, column.MaxLength);
            return new ColumnValue { Column = column, State = ValueState.Value, Value = value, RawBytes = raw };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or ArgumentException or OverflowException)
        {
            return new ColumnValue { Column = column, State = ValueState.Error, Note = ex.Message };
        }
    }
}
