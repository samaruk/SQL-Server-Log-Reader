using SqlLogReader.Decoding;
using SqlLogReader.Schema;

namespace SqlLogReader.Analysis;

/// <summary>Column values that can be used to find a changed row again (WHERE clause).</summary>
public sealed class RowIdentification
{
    public required IReadOnlyList<ColumnValue> Values { get; init; }

    /// <summary>True when the values come from a primary key / unique index and match at most one row.</summary>
    public bool IsUnique { get; init; }

    /// <summary>
    /// Picks the best identifying values for a change: the primary key (or another unique key) when its values
    /// are known, otherwise every comparable column of a full row image, otherwise the clustered key.
    /// </summary>
    /// <param name="preferBefore">Use the row as it was before the change (true) or after it (false).</param>
    public static RowIdentification? For(RowChange change, bool preferBefore)
    {
        var images = new List<DecodedRow>();
        var first = preferBefore ? change.Before : change.After;
        var second = preferBefore ? change.After : change.Before;
        if (first != null) images.Add(first);
        if (second != null) images.Add(second);
        if (change.Key != null) images.Add(change.Key);

        var uniqueKey = change.Table.UniqueKeyColumns;
        if (uniqueKey.Count > 0)
        {
            foreach (var image in images)
            {
                var values = uniqueKey.Select(name => image[name]).ToList();
                if (values.All(v => v is { HasValue: true }))
                    return new RowIdentification { Values = values!, IsUnique = true };
            }
        }

        foreach (var image in images.Where(i => i != change.Key))
        {
            var values = image.Values.Where(v => v.HasValue && IsComparable(v.Column)).ToList();
            if (values.Count > 0 && image.Values.All(v => v.State != ValueState.Error))
                return new RowIdentification { Values = values, IsUnique = false };
        }

        if (change.Key != null && change.Key.Values.All(v => v.HasValue))
            return new RowIdentification { Values = change.Key.Values, IsUnique = false };

        return null;
    }

    /// <summary>Whether a column can appear in an equality predicate.</summary>
    public static bool IsComparable(ColumnInfo column) =>
        !column.IsLargeObjectType
        && column.SystemType != SqlSystemType.ClrType
        && column.SystemType != SqlSystemType.Xml;
}
