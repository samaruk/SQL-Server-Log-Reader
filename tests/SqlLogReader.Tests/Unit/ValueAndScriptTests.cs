using System.Data.SqlTypes;
using SqlLogReader.Analysis;
using SqlLogReader.Decoding;
using SqlLogReader.Log;
using SqlLogReader.Schema;
using SqlLogReader.Scripting;
using static SqlLogReader.Tests.Unit.TestSchemas;

namespace SqlLogReader.Tests.Unit;

public class ValueAndScriptTests
{
    [Fact]
    public void Decodes_temporal_types()
    {
        // datetime: 1/300 s ticks then days since 1900-01-01
        var dt = BitConverter.GetBytes(300 * 3600 + 2).Concat(BitConverter.GetBytes(45000)).ToArray();
        Assert.Equal(new DateTime(1900, 1, 1).AddDays(45000).AddHours(1).AddMilliseconds(7),
            ValueDecoder.Decode(SqlSystemType.DateTime, dt, 0, 0, 0));

        // smalldatetime: minutes then days
        var sdt = BitConverter.GetBytes((ushort)61).Concat(BitConverter.GetBytes((ushort)2)).ToArray();
        Assert.Equal(new DateTime(1900, 1, 3, 1, 1, 0), ValueDecoder.Decode(SqlSystemType.SmallDateTime, sdt, 0, 0, 0));

        // time(7): 100ns units in 5 bytes
        long ticks = new TimeSpan(0, 23, 59, 59, 999).Ticks + 9999;
        var time = BitConverter.GetBytes(ticks).Take(5).ToArray();
        Assert.Equal(TimeSpan.FromTicks(ticks), ValueDecoder.Decode(SqlSystemType.Time, time, 0, 7, 0));

        // datetimeoffset(0): UTC time + date, then the offset in minutes
        var utc = new DateTime(2026, 10, 4, 15, 30, 0);
        var seconds = BitConverter.GetBytes((int)utc.TimeOfDay.TotalSeconds).Take(3);
        var days = BitConverter.GetBytes(DateOnly.FromDateTime(utc).DayNumber).Take(3);
        var offset = BitConverter.GetBytes((short)-330);
        var dto = (DateTimeOffset)ValueDecoder.Decode(SqlSystemType.DateTimeOffset, seconds.Concat(days).Concat(offset).ToArray(), 0, 0, 0);
        Assert.Equal(TimeSpan.FromMinutes(-330), dto.Offset);
        Assert.Equal(utc, dto.UtcDateTime);
    }

    [Fact]
    public void Decodes_money_and_large_decimals()
    {
        var money = BitConverter.GetBytes(long.MinValue);
        Assert.Equal(-922337203685477.5808m, ValueDecoder.Decode(SqlSystemType.Money, money, 0, 0, 0));

        // 38-digit value that does not fit System.Decimal stays a SqlDecimal.
        // 10^38 - 1 = 0x4B3B4CA85A86C47A098A223FFFFFFFFF, stored after a sign byte (1 = positive), little endian.
        var big = Hex("01" + "FFFFFFFF" + "3F228A09" + "7AC4865A" + "A84C3B4B");
        var value = ValueDecoder.Decode(SqlSystemType.Numeric, big, 38, 0, 0);
        var sqlDecimal = Assert.IsType<SqlDecimal>(value);
        Assert.Equal(new string('9', 38), sqlDecimal.ToString());
    }

    [Fact]
    public void Formats_literals()
    {
        var nvarchar = new ColumnInfo { Name = "n", SystemType = SqlSystemType.NVarChar, TypeName = "nvarchar" };
        var varchar = new ColumnInfo { Name = "v", SystemType = SqlSystemType.VarChar, TypeName = "varchar" };
        var dt2 = new ColumnInfo { Name = "d", SystemType = SqlSystemType.DateTime2, TypeName = "datetime2", Scale = 3 };
        var hid = new ColumnInfo { Name = "h", SystemType = SqlSystemType.ClrType, TypeName = "hierarchyid" };

        Assert.Equal("N'O''Brien'", SqlLiteral.Format(nvarchar, "O'Brien"));
        Assert.Equal("'plain'", SqlLiteral.Format(varchar, "plain"));
        Assert.Equal("N'Zoë'", SqlLiteral.Format(varchar, "Zoë"));
        Assert.Equal("'2026-10-04T10:08:46.519'", SqlLiteral.Format(dt2, new DateTime(2026, 10, 4, 10, 8, 46, 519)));
        Assert.Equal("CAST(0x58 AS hierarchyid)", SqlLiteral.Format(hid, new byte[] { 0x58 }));
        Assert.Equal("3.14", SqlLiteral.FormatValue(SqlSystemType.Float, 3.14));
        Assert.Equal("CAST(5 AS int)", SqlLiteral.FormatValue(SqlSystemType.SqlVariant, new SqlVariantValue(SqlSystemType.Int, 5, 0, 0)));
    }

    [Fact]
    public void Generates_undo_and_redo_statements()
    {
        var table = Patients();
        Assert.True(RowDecoder.TryDecode(table, Hex(CapturedRecords.PatientRow2), out var before, out _));
        Assert.True(LogRecordPayload.TryParseElements(Hex(CapturedRecords.PatientModifyColumnsRecord), out var elements));
        Assert.True(RowPatch.TryFromModifyColumns(elements, out var patch));
        Assert.True(patch.TryApply(Hex(CapturedRecords.PatientRow2), out var afterBytes));
        Assert.True(RowDecoder.TryDecode(table, afterBytes, out var after, out _));

        var tx = new TransactionInfo { TransactionId = "0000:00000383", Name = "UPDATE", State = TransactionState.Committed };
        var change = new RowChange
        {
            Lsn = Lsn.Parse("0000002A:00000178:0002"),
            Operation = ChangeOperation.Update,
            Table = table,
            Transaction = tx,
            Record = new LogRecord(),
            Before = before,
            After = after,
            Columns = before.Values.Select((b, i) => new ColumnChange
            {
                Column = b.Column, Before = b, After = after.Values[i], IsChanged = !b.SameAs(after.Values[i]),
            }).ToList(),
            IsComplete = true,
        };

        Assert.Equal(new[] { "LastName", "Balance" }, change.ChangedColumns.Select(c => c.Name));
        Assert.Equal("UPDATE [dbo].[Patients] SET [LastName] = N'Ahmed', [Balance] = 99.99 WHERE [PatientId] = 2;",
            ScriptGenerator.Generate(change, ScriptKind.Redo));
        Assert.Equal("UPDATE [dbo].[Patients] SET [LastName] = NULL, [Balance] = NULL WHERE [PatientId] = 2;",
            ScriptGenerator.Generate(change, ScriptKind.Undo));

        var delete = new RowChange
        {
            Lsn = Lsn.Zero, Operation = ChangeOperation.Delete, Table = table, Transaction = tx, Record = new LogRecord(),
            Before = before, Columns = Array.Empty<ColumnChange>(), IsComplete = true,
        };
        var undo = ScriptGenerator.Generate(delete, ScriptKind.Undo);
        Assert.Contains("SET IDENTITY_INSERT [dbo].[Patients] ON;", undo);
        Assert.Contains("VALUES (2, N'Sara', NULL, NULL, NULL, 1, NULL, NULL, '2026-10-04T10:08:46.519', NULL, NULL, 1);", undo);
        Assert.Equal("DELETE FROM [dbo].[Patients] WHERE [PatientId] = 2;", ScriptGenerator.Generate(delete, ScriptKind.Redo));
    }

    [Fact]
    public void Parses_dbcc_page_slot_dumps()
    {
        const string parent = "Slot 0, Offset 0xeb, Length 30, DumpStyle BYTE";
        var lines = new[]
        {
            (parent, "0000000000000000:   30007000 01000000 08000000 70202020 20202020  0.p.........p       "),
            (parent, "0000000000000014:   20202020 06000003 008f                        ...... ..."),
            ("Slot 1, Offset 0x200, Length 4, DumpStyle BYTE", "0000000000000000:   41424344                                ABCD"),
        };
        var slots = SqlPageRowSource.ParseSlotDumps(lines);
        Assert.Equal(Hex("30007000010000000800000070202020202020202020202006000003008F"), slots[0]);
        Assert.Equal(Hex("41424344"), slots[1]);
    }
}
