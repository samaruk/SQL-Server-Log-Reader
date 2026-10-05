namespace SqlLogReader.Schema;

/// <summary>
/// The in-row data allocation unit of a heap or clustered index partition, with its physical columns.
/// </summary>
public sealed class TableInfo
{
    public long AllocationUnitId { get; init; }
    public long PartitionId { get; init; }
    public int ObjectId { get; init; }
    public int IndexId { get; init; }
    public int PartitionNumber { get; init; }
    public string SchemaName { get; init; } = "dbo";
    public string TableName { get; init; } = "";
    public string? IndexName { get; init; }

    /// <summary>0 = none, 1 = row, 2 = page, 3/4 = columnstore.</summary>
    public int DataCompression { get; init; }

    /// <summary>All physical columns, including dropped columns and the uniqueifier.</summary>
    public IReadOnlyList<ColumnInfo> Columns { get; init; } = Array.Empty<ColumnInfo>();

    /// <summary>Names of the primary key (or unique clustered index) columns, used to identify rows in scripts.</summary>
    public IReadOnlyList<string> UniqueKeyColumns { get; init; } = Array.Empty<string>();

    public bool IsHeap => IndexId == 0;

    public string QualifiedName => $"{Quote(SchemaName)}.{Quote(TableName)}";

    public string DisplayName => $"{SchemaName}.{TableName}";

    /// <summary>Columns of the table definition ordered by column id.</summary>
    public IReadOnlyList<ColumnInfo> UserColumns =>
        _userColumns ??= Columns.Where(c => c.IsUserColumn).OrderBy(c => c.ColumnId).ToArray();

    /// <summary>Clustered index key columns (including the uniqueifier) ordered by key position.</summary>
    public IReadOnlyList<ColumnInfo> ClusteredKeyColumns =>
        _keyColumns ??= Columns.Where(c => c.KeyOrdinal > 0).OrderBy(c => c.KeyOrdinal).ToArray();

    public bool HasIdentity => UserColumns.Any(c => c.IsIdentity);

    public bool IsCompressed => DataCompression != 0;

    private IReadOnlyList<ColumnInfo>? _userColumns;
    private IReadOnlyList<ColumnInfo>? _keyColumns;

    public ColumnInfo? FindColumn(string name) =>
        UserColumns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Matches "Table", "schema.Table" or "[schema].[Table]".</summary>
    public bool MatchesName(string filter)
    {
        var f = filter.Replace("[", "").Replace("]", "").Trim();
        if (f.Contains('.'))
            return string.Equals(f, DisplayName, StringComparison.OrdinalIgnoreCase);
        return string.Equals(f, TableName, StringComparison.OrdinalIgnoreCase);
    }

    public static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public override string ToString() => DisplayName;
}
