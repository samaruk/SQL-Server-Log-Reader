using System.Text;
using SqlLogReader.Decoding;
using SqlLogReader.Log;
using SqlLogReader.Schema;

namespace SqlLogReader.Analysis;

/// <summary>
/// Builds a stable identity for a physical row so that its inserts, updates and deletes can be chained:
/// the clustered key bytes (plus uniqueifier) for clustered tables, the RID (file:page:slot) for heaps.
/// </summary>
public static class RowIdentity
{
    public static string ForHeap(int fileId, int pageId, int slot) => $"R:{fileId}:{pageId}:{slot}";

    public static string? ForHeap(LogRecord record) =>
        record.TryGetPage(out var file, out var page) && record.SlotId is int slot ? ForHeap(file, page, slot) : null;

    /// <summary>Identity of a clustered row from its decoded key columns.</summary>
    public static string? ForClusteredKey(TableInfo table, DecodedRow row)
    {
        var sb = new StringBuilder("K:");
        foreach (var keyColumn in table.ClusteredKeyColumns)
        {
            if (keyColumn.IsUniqueifier)
            {
                sb.Append('u').Append(row.Uniqueifier).Append('|');
                continue;
            }
            var value = row[keyColumn.Name];
            if (value == null) return null;
            switch (value.State)
            {
                case ValueState.Null:
                    sb.Append("N|");
                    break;
                case ValueState.Value when value.RawBytes != null:
                    sb.Append(Convert.ToHexString(value.RawBytes)).Append('|');
                    break;
                default:
                    return null;
            }
        }
        return sb.ToString();
    }

    /// <summary>Identity of the row a log record refers to, given the decoded row image (inserts/deletes).</summary>
    public static string? ForRow(TableInfo table, LogRecord record, DecodedRow row) =>
        table.IsHeap ? ForHeap(record) : ForClusteredKey(table, row);
}
