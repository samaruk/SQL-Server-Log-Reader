using SqlLogReader.Schema;

namespace SqlLogReader.Tests.Unit;

/// <summary>Table layouts captured from sys.system_internals_partition_columns on SQL Server 2022.</summary>
internal static class TestSchemas
{
    /// <summary>
    /// CREATE TABLE dbo.Patients(PatientId int IDENTITY PRIMARY KEY, FirstName nvarchar(50) NOT NULL, LastName nvarchar(50) NULL,
    /// BirthDate date NULL, Balance decimal(10,2) NULL, IsActive bit NOT NULL DEFAULT 1, Code char(5) NULL, Notes varchar(max) NULL,
    /// CreatedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(), Guid uniqueidentifier NULL, Score float NULL, Flag2 bit NULL)
    /// </summary>
    public static TableInfo Patients(long allocationUnitId = 72057594045726720) => new()
    {
        AllocationUnitId = allocationUnitId,
        ObjectId = 901578250,
        IndexId = 1,
        SchemaName = "dbo",
        TableName = "Patients",
        UniqueKeyColumns = new[] { "PatientId" },
        Columns = new[]
        {
            Col("PatientId", 1, SqlSystemType.Int, "int", 4, leafOffset: 4, nullBit: 1, keyOrdinal: 1, internalOffset: 1, internalNullBit: 1, identity: true, nullable: false),
            Col("FirstName", 2, SqlSystemType.NVarChar, "nvarchar", 100, leafOffset: -1, nullBit: 2, nullable: false),
            Col("LastName", 3, SqlSystemType.NVarChar, "nvarchar", 100, leafOffset: -2, nullBit: 3),
            Col("BirthDate", 4, SqlSystemType.Date, "date", 3, leafOffset: 8, nullBit: 4),
            Col("Balance", 5, SqlSystemType.Decimal, "decimal", 9, leafOffset: 11, nullBit: 5, precision: 10, scale: 2),
            Col("IsActive", 6, SqlSystemType.Bit, "bit", 1, leafOffset: 20, nullBit: 6, nullable: false),
            Col("Code", 7, SqlSystemType.Char, "char", 5, leafOffset: 21, nullBit: 7, codePage: 1252),
            Col("Notes", 8, SqlSystemType.VarChar, "varchar", -1, leafOffset: -3, nullBit: 8, codePage: 1252),
            Col("CreatedAt", 9, SqlSystemType.DateTime2, "datetime2", 7, leafOffset: 26, nullBit: 9, scale: 3, nullable: false),
            Col("Guid", 10, SqlSystemType.UniqueIdentifier, "uniqueidentifier", 16, leafOffset: 33, nullBit: 10),
            Col("Score", 11, SqlSystemType.Float, "float", 8, leafOffset: 49, nullBit: 11, precision: 53),
            Col("Flag2", 12, SqlSystemType.Bit, "bit", 1, leafOffset: 20, nullBit: 12, bitPosition: 1),
        },
    };

    /// <summary>CREATE TABLE dbo.Heap1(id int NOT NULL, name varchar(20) NULL, qty int NULL)</summary>
    public static TableInfo Heap1() => new()
    {
        AllocationUnitId = 72057594045857792,
        IndexId = 0,
        SchemaName = "dbo",
        TableName = "Heap1",
        Columns = new[]
        {
            Col("id", 1, SqlSystemType.Int, "int", 4, leafOffset: 4, nullBit: 1, nullable: false),
            Col("name", 2, SqlSystemType.VarChar, "varchar", 20, leafOffset: -1, nullBit: 2, codePage: 1252),
            Col("qty", 3, SqlSystemType.Int, "int", 4, leafOffset: 8, nullBit: 3),
        },
    };

    /// <summary>CREATE TABLE dbo.NU(k int NOT NULL, v varchar(10)); CREATE CLUSTERED INDEX cx ON dbo.NU(k)</summary>
    public static TableInfo NonUnique() => new()
    {
        AllocationUnitId = 1,
        IndexId = 1,
        SchemaName = "dbo",
        TableName = "NU",
        Columns = new[]
        {
            new ColumnInfo
            {
                Name = "(uniqueifier)", ColumnId = 0, SystemType = SqlSystemType.Int, TypeName = "int", MaxLength = 4,
                LeafOffset = -1, LeafNullBit = 1, InternalOffset = -1, InternalNullBit = 0, KeyOrdinal = 2, IsUniqueifier = true,
            },
            Col("k", 1, SqlSystemType.Int, "int", 4, leafOffset: 4, nullBit: 2, keyOrdinal: 1, internalOffset: 1, internalNullBit: 1, nullable: false),
            Col("v", 2, SqlSystemType.VarChar, "varchar", 10, leafOffset: -2, nullBit: 3, codePage: 1252),
        },
    };

    private static ColumnInfo Col(string name, int id, SqlSystemType type, string typeName, short maxLength, short leafOffset, int nullBit,
        byte precision = 0, byte scale = 0, byte bitPosition = 0, int keyOrdinal = 0, short internalOffset = 0, int internalNullBit = 0,
        int codePage = 0, bool identity = false, bool nullable = true) => new()
    {
        Name = name,
        ColumnId = id,
        SystemType = type,
        TypeName = typeName,
        MaxLength = maxLength,
        Precision = precision,
        Scale = scale,
        CodePage = codePage,
        LeafOffset = leafOffset,
        LeafNullBit = nullBit,
        LeafBitPosition = bitPosition,
        KeyOrdinal = keyOrdinal,
        InternalOffset = internalOffset,
        InternalNullBit = internalNullBit,
        IsIdentity = identity,
        IsNullable = nullable,
    };

    public static byte[] Hex(string hex) => Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
}
