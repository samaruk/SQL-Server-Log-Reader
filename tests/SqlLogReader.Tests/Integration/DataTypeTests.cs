using SqlLogReader.Analysis;
using SqlLogReader.Decoding;
using SqlLogReader.Scripting;

namespace SqlLogReader.Tests.Integration;

public class DataTypeTests
{
    private const string CreateTable = """
        CREATE TABLE dbo.AllTypes (
            id int IDENTITY(1,1) PRIMARY KEY,
            c_tinyint tinyint NULL, c_smallint smallint NULL, c_int int NULL, c_bigint bigint NULL,
            c_bit bit NULL, c_bit2 bit NULL,
            c_decimal decimal(18,4) NULL, c_numeric numeric(38,10) NULL, c_decimal5 decimal(5,2) NULL,
            c_money money NULL, c_smallmoney smallmoney NULL,
            c_float float NULL, c_real real NULL,
            c_date date NULL, c_time time(7) NULL, c_time0 time(0) NULL, c_datetime datetime NULL, c_smalldatetime smalldatetime NULL,
            c_datetime2 datetime2(7) NULL, c_datetime2_3 datetime2(3) NULL, c_dto datetimeoffset(7) NULL, c_dto0 datetimeoffset(0) NULL,
            c_guid uniqueidentifier NULL,
            c_char char(10) NULL, c_varchar varchar(100) NULL, c_nchar nchar(10) NULL, c_nvarchar nvarchar(100) NULL,
            c_binary binary(8) NULL, c_varbinary varbinary(50) NULL,
            c_varcharmax varchar(max) NULL, c_nvarcharmax nvarchar(max) NULL, c_varbinarymax varbinary(max) NULL,
            c_variant sql_variant NULL,
            c_hierarchy hierarchyid NULL,
            c_utf8 varchar(50) COLLATE Latin1_General_100_CI_AS_SC_UTF8 NULL,
            c_greek varchar(50) COLLATE Greek_CI_AS NULL,
            c_rowversion rowversion,
            c_computed AS (c_int * 2),
            c_persisted AS (c_smallint + 1) PERSISTED
        );
        """;

    private const string InsertRows = """
        INSERT INTO dbo.AllTypes (c_tinyint, c_smallint, c_int, c_bigint, c_bit, c_bit2, c_decimal, c_numeric, c_decimal5, c_money, c_smallmoney,
            c_float, c_real, c_date, c_time, c_time0, c_datetime, c_smalldatetime, c_datetime2, c_datetime2_3, c_dto, c_dto0, c_guid,
            c_char, c_varchar, c_nchar, c_nvarchar, c_binary, c_varbinary, c_varcharmax, c_nvarcharmax, c_varbinarymax, c_variant, c_hierarchy, c_utf8, c_greek)
        VALUES
        (7, 1234, 123456789, 1234567890123, 1, 0, 12345.6789, 1234567890123456.0123456789, 123.45, 1234.5678, 12.34,
         3.14159265358979, 2.5, '2026-10-04', '13:45:30.1234567', '08:00:00', '2026-10-04 13:45:30.997', '2026-10-04 13:45',
         '2026-10-04 13:45:30.1234567', '2026-10-04 13:45:30.123', '2026-10-04 13:45:30.1234567 +05:00', '2026-01-01 00:00:00 -08:00',
         '6F9619FF-8B86-D011-B42D-00C04FC964FF', 'abc', 'Hello, ''quoted'' world', N'Ωμέγα', N'Unicode ☃ مرحبا 日本',
         0x0102030405060708, 0xDEADBEEF, REPLICATE('x', 50), N'short nvarchar(max)', 0xCAFE, CAST(CAST(42 AS int) AS sql_variant),
         hierarchyid::Parse('/1/2/'), N'UTF-8 ✓ ñ', N'αβγ'),
        (NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL,
         NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL),
        (255, -32768, -2147483648, 9223372036854775807, 1, 1, -99999999999999.9999, -0.0000000001, -999.99, -922337203685477.5808, 214748.3647,
         -1.7976931348623157E+308, -3.4E+38, '0001-01-01', '23:59:59.9999999', '23:59:59', '1753-01-01 00:00:00.003', '2079-06-06 23:59',
         '9999-12-31 23:59:59.9999999', '0001-01-01 00:00:00.000', '0001-01-01 00:00:00 -14:00', '9999-12-31 23:59:59 +14:00',
         '00000000-0000-0000-0000-000000000000', '', '', N'', N'',
         0x00, 0x, '', N'', 0x, CAST(CAST(N'variant text' AS nvarchar(20)) AS sql_variant),
         hierarchyid::GetRoot(), '', '');
        """;

    private const string SelectAll = """
        SELECT id, c_tinyint, c_smallint, c_int, c_bigint, c_bit, c_bit2, c_decimal, c_numeric, c_decimal5, c_money, c_smallmoney,
               c_float, c_real, c_date, c_time, c_time0, c_datetime, c_smalldatetime, c_datetime2, c_datetime2_3, c_dto, c_dto0, c_guid,
               c_char, c_varchar, c_nchar, c_nvarchar, c_binary, c_varbinary, c_varcharmax, c_nvarcharmax, c_varbinarymax, c_variant,
               CAST(c_hierarchy AS varbinary(892)) AS c_hierarchy, c_utf8, c_greek, c_rowversion, c_persisted
        FROM dbo.AllTypes ORDER BY id;
        """;

    [SqlServerFact]
    public async Task Inserted_and_deleted_rows_decode_to_the_stored_values_and_undo_restores_them()
    {
        await using var db = await TestDatabase.CreateAsync("lr_types");
        // UTF-8 collations exist from SQL Server 2019 (version 15) on.
        var version = await db.QueryAsync("SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion')) AS v");
        bool utf8 = Convert.ToInt32(version[0]["v"]) >= 15;
        string Adapt(string sql) => utf8 ? sql : sql.Replace("COLLATE Latin1_General_100_CI_AS_SC_UTF8", "COLLATE Latin1_General_100_CI_AS_SC");
        await db.ExecuteAsync(Adapt(CreateTable));
        await db.ExecuteAsync(InsertRows);
        var original = await db.QueryAsync(SelectAll);
        Assert.Equal(3, original.Count);

        await db.ExecuteAsync("DELETE FROM dbo.AllTypes;");
        var result = await db.ReadLogAsync(new AnalyzerOptions { Tables = new[] { "AllTypes" } });
        Assert.Empty(result.Warnings);

        var inserts = result.Changes.Where(c => c.Operation == ChangeOperation.Insert).ToList();
        var deletes = result.Changes.Where(c => c.Operation == ChangeOperation.Delete).ToList();
        Assert.Equal(3, inserts.Count);
        Assert.Equal(3, deletes.Count);

        for (int i = 0; i < 3; i++)
        {
            var expected = original[i];
            var inserted = inserts.Single(c => Equals(c.After!["id"]!.Value, expected["id"]));
            var deleted = deletes.Single(c => Equals(c.Before!["id"]!.Value, expected["id"]));
            Assert.True(inserted.IsComplete && deleted.IsComplete);
            foreach (var (name, value) in expected)
            {
                Comparison.AssertSameValue(value, inserted.After![name]!, $"insert row {i} {name}");
                Comparison.AssertSameValue(value, deleted.Before![name]!, $"delete row {i} {name}");
            }
            // Non-persisted computed columns are not stored in the row at all.
            Assert.Null(deleted.Before!["c_computed"]);
        }

        // Undo the deletes and compare the restored table with the original one.
        await db.ExecuteAsync(ScriptGenerator.GenerateScript(deletes, ScriptKind.Undo));
        var restored = await db.QueryAsync(SelectAll);
        Assert.Equal(original.Count, restored.Count);
        for (int i = 0; i < original.Count; i++)
        {
            foreach (var (name, value) in original[i])
            {
                if (name == "c_rowversion") continue; // a new rowversion is generated on insert
                Assert.Equal(value, restored[i][name]);
            }
        }
    }

    [SqlServerFact]
    public async Task Off_row_values_are_reported_as_unavailable()
    {
        await using var db = await TestDatabase.CreateAsync("lr_lob");
        await db.ExecuteAsync("CREATE TABLE dbo.Docs(id int PRIMARY KEY, title varchar(20), body varchar(max), legacy text, ov varchar(8000), ov2 varchar(8000));");
        await db.ExecuteAsync("INSERT dbo.Docs VALUES (1, 'big', REPLICATE(CAST('a' AS varchar(max)), 20000), 'old text', REPLICATE('b', 5000), REPLICATE('c', 5000));");

        var result = await db.ReadLogAsync();
        var insert = Assert.Single(result.Changes);
        Assert.Equal("big", insert.After!["title"]!.Value);
        Assert.Equal(ValueState.Unavailable, insert.After!["body"]!.State);
        Assert.Equal(ValueState.Unavailable, insert.After!["legacy"]!.State);
        // One of the two 5000-byte columns is pushed off-row (row-overflow).
        Assert.Contains(new[] { "ov", "ov2" }, n => insert.After![n]!.State == ValueState.Unavailable);
        Assert.Contains("WARNING", ScriptGenerator.Generate(insert, ScriptKind.Redo));
    }
}
