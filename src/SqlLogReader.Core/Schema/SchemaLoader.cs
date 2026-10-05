using Microsoft.Data.SqlClient;

namespace SqlLogReader.Schema;

/// <summary>Loads the physical layout of every user table from the database catalog.</summary>
public static class SchemaLoader
{
    private const string TablesSql = """
        SELECT au.allocation_unit_id, p.partition_id, p.object_id, p.index_id, p.partition_number,
               CONVERT(int, p.data_compression) AS data_compression,
               s.name AS schema_name, o.name AS table_name, i.name AS index_name
        FROM sys.allocation_units AS au
        JOIN sys.partitions AS p ON au.type = 1 AND au.container_id = p.hobt_id
        JOIN sys.objects AS o ON o.object_id = p.object_id
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        LEFT JOIN sys.indexes AS i ON i.object_id = p.object_id AND i.index_id = p.index_id
        WHERE p.index_id IN (0, 1) AND o.type = 'U';
        """;

    private const string ColumnsSqlTemplate = """
        SELECT pc.partition_id, pc.partition_column_id, pc.is_dropped, pc.is_uniqueifier,
               pc.system_type_id, pc.max_length, pc.precision, pc.scale, pc.collation_name,
               CONVERT(int, COLLATIONPROPERTY(pc.collation_name, 'CodePage')) AS code_page,
               pc.leaf_offset, pc.leaf_null_bit, pc.leaf_bit_position,
               pc.internal_offset, pc.internal_null_bit, pc.internal_bit_position,
               pc.key_ordinal, pc.is_sparse, pc.is_filestream, {0} AS default_value,
               c.name AS column_name, c.is_nullable, c.is_identity, c.is_computed,
               TYPE_NAME(pc.system_type_id) AS base_type_name, TYPE_NAME(c.user_type_id) AS user_type_name
        FROM sys.system_internals_partition_columns AS pc
        JOIN sys.partitions AS p ON p.partition_id = pc.partition_id
        JOIN sys.objects AS o ON o.object_id = p.object_id
        LEFT JOIN sys.columns AS c
               ON c.object_id = p.object_id AND c.column_id = pc.partition_column_id AND pc.is_dropped = 0
        WHERE p.index_id IN (0, 1) AND o.type = 'U';
        """;

    private const string UniqueKeysSql = """
        SELECT i.object_id, i.index_id, c.name
        FROM sys.indexes AS i
        JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.objects AS o ON o.object_id = i.object_id
        WHERE o.type = 'U' AND i.is_unique = 1 AND i.has_filter = 0 AND i.is_disabled = 0 AND i.is_hypothetical = 0
        ORDER BY i.object_id,
                 CASE WHEN i.is_primary_key = 1 THEN 0 WHEN i.index_id = 1 THEN 1 ELSE 2 END,
                 i.index_id, ic.key_ordinal;
        """;

    /// <summary>Loads the schema of the database the connection is currently using.</summary>
    public static async Task<DatabaseSchema> LoadAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        var tables = new List<(long au, long partition, int objectId, int indexId, int partitionNumber, int compression, string schema, string table, string? index)>();
        await using (var cmd = new SqlCommand(TablesSql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)));
            }
        }

        await LoadCollationsAsync(connection, cancellationToken);
        var columnsByPartition = await LoadColumnsAsync(connection, cancellationToken);
        var keysByObject = await LoadUniqueKeysAsync(connection, cancellationToken);

        var result = tables.Select(t => new TableInfo
        {
            AllocationUnitId = t.au,
            PartitionId = t.partition,
            ObjectId = t.objectId,
            IndexId = t.indexId,
            PartitionNumber = t.partitionNumber,
            DataCompression = t.compression,
            SchemaName = t.schema,
            TableName = t.table,
            IndexName = t.index,
            Columns = columnsByPartition.TryGetValue(t.partition, out var cols) ? cols : new List<ColumnInfo>(),
            UniqueKeyColumns = keysByObject.TryGetValue(t.objectId, out var keys) ? keys : Array.Empty<string>(),
        });

        return new DatabaseSchema(connection.Database, result);
    }

    /// <summary>Collation id → code page, needed to decode char/varchar values stored in sql_variant columns.</summary>
    private static async Task LoadCollationsAsync(SqlConnection connection, CancellationToken ct)
    {
        if (Decoding.CollationCodePages.Count > 0) return;
        const string sql = """
            SELECT CONVERT(int, COLLATIONPROPERTY(name, 'CollationId')), CONVERT(int, COLLATIONPROPERTY(name, 'CodePage'))
            FROM sys.fn_helpcollations();
            """;
        try
        {
            await using var cmd = new SqlCommand(sql, connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                    Decoding.CollationCodePages.Register(reader.GetInt32(0), reader.GetInt32(1));
            }
        }
        catch (SqlException)
        {
            // Not essential: sql_variant strings fall back to code page 1252.
        }
    }

    private static async Task<Dictionary<long, List<ColumnInfo>>> LoadColumnsAsync(SqlConnection connection, CancellationToken ct)
    {
        try
        {
            return await ReadColumnsAsync(connection, string.Format(ColumnsSqlTemplate, "pc.default_value"), ct);
        }
        catch (SqlException ex) when (ex.Number == 207)
        {
            // default_value only exists on SQL Server 2012 and later.
            return await ReadColumnsAsync(connection, string.Format(ColumnsSqlTemplate, "CAST(NULL AS sql_variant)"), ct);
        }
    }

    private static async Task<Dictionary<long, List<ColumnInfo>>> ReadColumnsAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        var result = new Dictionary<long, List<ColumnInfo>>();
        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long partitionId = reader.GetInt64(0);
            int columnId = reader.GetInt32(1);
            bool isDropped = reader.GetBoolean(2);
            bool isUniqueifier = reader.GetBoolean(3);
            var systemType = (SqlSystemType)reader.GetByte(4);
            string? columnName = reader.IsDBNull(20) ? null : reader.GetString(20);
            string baseTypeName = reader.IsDBNull(24) ? systemType.ToString().ToLowerInvariant() : reader.GetString(24);
            string? userTypeName = reader.IsDBNull(25) ? null : reader.GetString(25);

            var column = new ColumnInfo
            {
                Name = isUniqueifier ? "(uniqueifier)" : columnName ?? $"(dropped column {columnId})",
                ColumnId = columnId,
                IsDropped = isDropped || (!isUniqueifier && columnName == null),
                IsUniqueifier = isUniqueifier,
                SystemType = systemType,
                TypeName = systemType == SqlSystemType.ClrType && userTypeName != null ? userTypeName : baseTypeName,
                MaxLength = reader.GetInt16(5),
                Precision = reader.GetByte(6),
                Scale = reader.GetByte(7),
                CollationName = reader.IsDBNull(8) ? null : reader.GetString(8),
                CodePage = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                LeafOffset = reader.GetInt16(10),
                LeafNullBit = Convert.ToInt32(reader.GetValue(11)),
                LeafBitPosition = reader.GetByte(12),
                InternalOffset = reader.GetInt16(13),
                InternalNullBit = Convert.ToInt32(reader.GetValue(14)),
                InternalBitPosition = reader.GetByte(15),
                KeyOrdinal = reader.IsDBNull(16) ? 0 : Convert.ToInt32(reader.GetValue(16)),
                IsSparse = reader.GetBoolean(17),
                IsFilestream = reader.GetBoolean(18),
                DefaultValue = reader.IsDBNull(19) ? null : reader.GetValue(19),
                IsNullable = reader.IsDBNull(21) || reader.GetBoolean(21),
                IsIdentity = !reader.IsDBNull(22) && reader.GetBoolean(22),
                IsComputed = !reader.IsDBNull(23) && reader.GetBoolean(23),
            };

            if (!result.TryGetValue(partitionId, out var list))
                result[partitionId] = list = new List<ColumnInfo>();
            list.Add(column);
        }
        return result;
    }

    private static async Task<Dictionary<int, IReadOnlyList<string>>> LoadUniqueKeysAsync(SqlConnection connection, CancellationToken ct)
    {
        // Rows arrive ordered by preference; keep the first index seen for every table.
        var chosenIndex = new Dictionary<int, int>();
        var result = new Dictionary<int, List<string>>();
        await using var cmd = new SqlCommand(UniqueKeysSql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            int objectId = reader.GetInt32(0);
            int indexId = reader.GetInt32(1);
            if (!chosenIndex.TryGetValue(objectId, out var chosen))
            {
                chosenIndex[objectId] = chosen = indexId;
                result[objectId] = new List<string>();
            }
            if (chosen == indexId)
                result[objectId].Add(reader.GetString(2));
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }
}
