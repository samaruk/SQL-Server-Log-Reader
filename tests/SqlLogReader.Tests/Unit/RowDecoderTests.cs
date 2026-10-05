using SqlLogReader.Analysis;
using SqlLogReader.Decoding;
using static SqlLogReader.Tests.Unit.TestSchemas;

namespace SqlLogReader.Tests.Unit;

public class RowDecoderTests
{
    [Fact]
    public void Decodes_every_column_of_a_clustered_row()
    {
        Assert.True(RowDecoder.TryDecode(Patients(), Hex(CapturedRecords.PatientRow1), out var row, out var error), error);

        Assert.Equal(1, row["PatientId"]!.Value);
        Assert.Equal("Ali", row["FirstName"]!.Value);
        Assert.Equal("Khan", row["LastName"]!.Value);
        Assert.Equal(new DateOnly(1990, 5, 17), row["BirthDate"]!.Value);
        Assert.Equal(1234.56m, row["Balance"]!.Value);
        Assert.Equal(true, row["IsActive"]!.Value);
        Assert.Equal("AB123", row["Code"]!.Value);
        Assert.Equal("first note", row["Notes"]!.Value);
        Assert.Equal(new DateTime(2026, 10, 4, 10, 8, 46, 519), row["CreatedAt"]!.Value);
        Assert.Equal(Guid.Parse("A9E79E90-05EA-4021-8959-50C4689F3241"), row["Guid"]!.Value);
        Assert.Equal(3.14, row["Score"]!.Value);
        Assert.Equal(false, row["Flag2"]!.Value);
    }

    [Fact]
    public void Honours_the_null_bitmap_and_ignores_stale_bytes()
    {
        Assert.True(RowDecoder.TryDecode(Patients(), Hex(CapturedRecords.PatientRow2), out var row, out _));

        Assert.Equal("Sara", row["FirstName"]!.Value);
        foreach (var name in new[] { "LastName", "BirthDate", "Balance", "Code", "Notes", "Guid", "Score" })
            Assert.True(row[name]!.IsNull, name + " should be NULL");
        Assert.Equal(true, row["IsActive"]!.Value);
        Assert.Equal(true, row["Flag2"]!.Value);
    }

    [Fact]
    public void Reads_uniqueifier_of_non_unique_clustered_rows()
    {
        var table = NonUnique();
        Assert.True(RowDecoder.TryDecode(table, Hex(CapturedRecords.NonUniqueRowX), out var x, out _));
        Assert.True(RowDecoder.TryDecode(table, Hex(CapturedRecords.NonUniqueRowY), out var y, out _));

        Assert.Equal("x", x["v"]!.Value);
        Assert.Equal(0, x.Uniqueifier);
        Assert.Equal("y", y["v"]!.Value);
        Assert.Equal(1, y.Uniqueifier);
        Assert.NotEqual(RowIdentity.ForClusteredKey(table, x), RowIdentity.ForClusteredKey(table, y));
    }

    [Fact]
    public void Decodes_clustered_key_carried_by_update_records()
    {
        Assert.True(RowDecoder.TryDecodeKey(Patients(), Hex("1602000000010000"), out var key));
        Assert.Equal(2, key["PatientId"]!.Value);

        var table = NonUnique();
        Assert.True(RowDecoder.TryDecodeKey(table, Hex(CapturedRecords.NonUniqueKeyY), out var nuKey));
        Assert.Equal(5, nuKey["k"]!.Value);
        Assert.Equal(1, nuKey.Uniqueifier);

        // The key from the update record and the inserted row must produce the same identity.
        Assert.True(RowDecoder.TryDecode(table, Hex(CapturedRecords.NonUniqueRowY), out var row, out _));
        Assert.Equal(RowIdentity.ForClusteredKey(table, row), RowIdentity.ForClusteredKey(table, nuKey));
    }

    [Fact]
    public void Rejects_compressed_tables()
    {
        var table = Patients();
        var compressed = new SqlLogReader.Schema.TableInfo
        {
            AllocationUnitId = table.AllocationUnitId, IndexId = 1, TableName = "Patients", Columns = table.Columns, DataCompression = 2,
        };
        Assert.False(RowDecoder.TryDecode(compressed, Hex(CapturedRecords.PatientRow1), out _, out var error));
        Assert.Contains("compress", error);
    }

    [Fact]
    public void Columns_added_after_the_row_was_written_are_null()
    {
        var table = Heap1();
        // Row written when the table only had (id, name): column count 2.
        var row = Hex("30000800070000000200000100100078");
        Assert.True(RowDecoder.TryDecode(table, row, out var decoded, out var error), error);
        Assert.Equal(7, decoded["id"]!.Value);
        Assert.Equal("x", decoded["name"]!.Value);
        Assert.True(decoded["qty"]!.IsNull);
    }
}
