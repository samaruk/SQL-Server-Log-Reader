namespace SqlLogReader.Schema;

/// <summary>
/// A physical column of a heap or clustered index partition
/// (<c>sys.system_internals_partition_columns</c> joined with <c>sys.columns</c>).
/// </summary>
public sealed class ColumnInfo
{
    /// <summary>Column name; dropped columns and the uniqueifier get synthetic names.</summary>
    public string Name { get; init; } = "";

    /// <summary><c>partition_column_id</c>; equals <c>sys.columns.column_id</c> for live columns.</summary>
    public int ColumnId { get; init; }

    public SqlSystemType SystemType { get; init; }

    /// <summary>Type name as written in DDL (e.g. <c>nvarchar</c>, <c>hierarchyid</c>).</summary>
    public string TypeName { get; init; } = "";

    /// <summary>Storage length in bytes (-1 for MAX types).</summary>
    public short MaxLength { get; init; }
    public byte Precision { get; init; }
    public byte Scale { get; init; }
    public string? CollationName { get; init; }

    /// <summary>Windows code page used for char/varchar data (65001 for UTF-8 collations).</summary>
    public int CodePage { get; init; }

    /// <summary>Byte offset in the row for fixed-length columns (&gt; 0) or the negated 1-based variable column index (&lt; 0).</summary>
    public short LeafOffset { get; init; }

    /// <summary>1-based position in the null bitmap.</summary>
    public int LeafNullBit { get; init; }
    public byte LeafBitPosition { get; init; }

    /// <summary>Same as the Leaf* values but for index (key) records.</summary>
    public short InternalOffset { get; init; }
    public int InternalNullBit { get; init; }
    public byte InternalBitPosition { get; init; }

    /// <summary>Position in the clustered index key (0 when not a key column).</summary>
    public int KeyOrdinal { get; init; }

    public bool IsDropped { get; init; }
    public bool IsUniqueifier { get; init; }
    public bool IsNullable { get; init; } = true;
    public bool IsIdentity { get; init; }
    public bool IsComputed { get; init; }
    public bool IsSparse { get; init; }
    public bool IsFilestream { get; init; }

    /// <summary>Value used for rows written before the column was added (<c>default_value</c>).</summary>
    public object? DefaultValue { get; init; }

    public bool IsFixedLength => LeafOffset > 0;
    public int VariableIndex => -LeafOffset;

    /// <summary>True for columns that exist in the table definition (not dropped, not the uniqueifier).</summary>
    public bool IsUserColumn => !IsDropped && !IsUniqueifier;

    public bool IsUnicode => SystemType is SqlSystemType.NChar or SqlSystemType.NVarChar or SqlSystemType.NText;

    public bool IsLargeObjectType =>
        SystemType is SqlSystemType.Text or SqlSystemType.NText or SqlSystemType.Image or SqlSystemType.Xml
        || (MaxLength == -1 && SystemType is SqlSystemType.VarChar or SqlSystemType.NVarChar or SqlSystemType.VarBinary);

    /// <summary>Type as it would appear in a column definition, e.g. <c>decimal(10,2)</c>.</summary>
    public string FullTypeName
    {
        get
        {
            switch (SystemType)
            {
                case SqlSystemType.Char:
                case SqlSystemType.VarChar:
                case SqlSystemType.Binary:
                case SqlSystemType.VarBinary:
                    return MaxLength == -1 ? $"{TypeName}(max)" : $"{TypeName}({MaxLength})";
                case SqlSystemType.NChar:
                case SqlSystemType.NVarChar:
                    return MaxLength == -1 ? $"{TypeName}(max)" : $"{TypeName}({MaxLength / 2})";
                case SqlSystemType.Decimal:
                case SqlSystemType.Numeric:
                    return $"{TypeName}({Precision},{Scale})";
                case SqlSystemType.Time:
                case SqlSystemType.DateTime2:
                case SqlSystemType.DateTimeOffset:
                    return $"{TypeName}({Scale})";
                default:
                    return TypeName;
            }
        }
    }

    public override string ToString() => $"{Name} {FullTypeName}";
}
