using SqlLogReader.Log;
using static SqlLogReader.Tests.Unit.TestSchemas;

namespace SqlLogReader.Tests.Unit;

public class LogRecordPayloadTests
{
    [Fact]
    public void Splits_insert_record_into_row_and_lock_elements()
    {
        Assert.True(LogRecordPayload.TryParseElements(Hex(CapturedRecords.PatientInsertRecord), out var elements));
        Assert.Equal(3, elements.Length);
        Assert.Equal(Hex(CapturedRecords.PatientRow1), elements[0]);
        Assert.Empty(elements[1]);
        Assert.Equal(26, elements[2].Length);
    }

    [Fact]
    public void Splits_modify_columns_record_into_all_elements()
    {
        Assert.True(LogRecordPayload.TryParseElements(Hex(CapturedRecords.PatientModifyColumnsRecord), out var elements));
        Assert.Equal(8, elements.Length);
        Assert.Equal(Hex("0C000C003B003B00"), elements[0]);
        Assert.Equal(Hex("03000E00"), elements[1]);
        Assert.Equal(Hex("1602000000010000"), elements[2]);
        Assert.Equal(Hex("40E201"), elements[4]);
        Assert.Equal(Hex("0F2700"), elements[5]);
        Assert.Equal(14, elements[6].Length);
        Assert.Equal(26, elements[7].Length);
    }

    [Fact]
    public void Compensation_record_has_empty_before_element()
    {
        Assert.True(LogRecordPayload.TryParseElements(Hex(CapturedRecords.HeapCompensationModifyRowRecord), out var elements));
        Assert.Equal(6, elements.Length);
        Assert.Empty(elements[0]);
        Assert.Equal(Hex("18006170706C"), elements[1]);
    }

    [Fact]
    public void Falls_back_to_rowlog_contents_when_record_bytes_do_not_match()
    {
        var record = new LogRecord
        {
            Operation = "LOP_INSERT_ROWS",
            RecordBytes = Hex("0000"),
            RowLogContents = new byte[]?[] { Hex("3000"), Array.Empty<byte>(), Hex("01"), null, null },
        };
        Assert.Equal(new[] { Hex("3000"), Array.Empty<byte>(), Hex("01") }, record.Elements);
    }

    [Fact]
    public void Rejects_truncated_records()
    {
        var bytes = Hex(CapturedRecords.PatientInsertRecord);
        Assert.False(LogRecordPayload.TryParseElements(bytes.AsSpan(0, 100), out _));
        Assert.False(LogRecordPayload.TryParseElements(Hex("0000"), out _));
    }
}
