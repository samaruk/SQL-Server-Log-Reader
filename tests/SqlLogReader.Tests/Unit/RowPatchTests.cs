using SqlLogReader.Decoding;
using SqlLogReader.Log;
using static SqlLogReader.Tests.Unit.TestSchemas;

namespace SqlLogReader.Tests.Unit;

public class RowPatchTests
{
    [Fact]
    public void Modify_columns_record_rebuilds_the_updated_row_and_back()
    {
        Assert.True(LogRecordPayload.TryParseElements(Hex(CapturedRecords.PatientModifyColumnsRecord), out var elements));
        Assert.True(RowPatch.TryFromModifyColumns(elements, out var patch));
        Assert.Equal(2, patch.Ranges.Count);

        var before = Hex(CapturedRecords.PatientRow2);
        Assert.True(patch.TryApply(before, out var after));
        Assert.True(RowDecoder.TryDecode(Patients(), after, out var row, out _));
        Assert.Equal("Ahmed", row["LastName"]!.Value);
        Assert.Equal(99.99m, row["Balance"]!.Value);
        Assert.Equal("Sara", row["FirstName"]!.Value);

        Assert.True(patch.TryRevert(after, out var reverted));
        Assert.Equal(before, reverted);
    }

    [Fact]
    public void Modify_row_record_rebuilds_the_updated_row_and_back()
    {
        Assert.True(LogRecordPayload.TryParseElements(Hex(CapturedRecords.HeapModifyRowRecord), out var elements));
        var patch = RowPatch.FromModifyRow(17, 8, elements[0], elements[1], isCompensation: false);
        var before = Hex(CapturedRecords.HeapRowBanana);

        Assert.True(patch.TryApply(before, out var after));
        Assert.True(RowDecoder.TryDecode(Heap1(), after, out var row, out _));
        Assert.Equal("blueberry", row["name"]!.Value);
        Assert.Equal(20, row["qty"]!.Value);

        Assert.True(patch.TryRevert(after, out var reverted));
        Assert.Equal(before, reverted);
    }

    [Fact]
    public void Patch_refuses_a_row_that_does_not_match()
    {
        Assert.True(LogRecordPayload.TryParseElements(Hex(CapturedRecords.HeapModifyRowRecord), out var elements));
        var patch = RowPatch.FromModifyRow(17, 8, elements[0], elements[1], isCompensation: false);
        var other = Hex("30000C00010000000A000000030000010018006170706C65"); // 'apple', not 'banana'
        Assert.False(patch.TryApply(other, out _));
        Assert.False(patch.TryRevert(other, out _));
    }

    [Fact]
    public void Compensation_patch_restores_the_row_without_before_bytes()
    {
        var apple = Hex("30000C00010000000B000000030000010018006170706C65");
        // UPDATE ... SET name = 'cherry-long-value': SQL Server logged only the differing bytes.
        var update = RowPatch.FromModifyRow(17, 6, Hex("18006170706C"), Hex("24006368657272792D6C6F6E672D76616C75"), isCompensation: false);
        Assert.True(update.TryApply(apple, out var cherry));
        Assert.True(RowDecoder.TryDecode(Heap1(), cherry, out var row, out _));
        Assert.Equal("cherry-long-value", row["name"]!.Value);

        Assert.True(LogRecordPayload.TryParseElements(Hex(CapturedRecords.HeapCompensationModifyRowRecord), out var elements));
        var clr = RowPatch.FromModifyRow(17, 18, elements[0], elements[1], isCompensation: true);
        Assert.False(clr.CanRevert);
        Assert.True(clr.TryApply(cherry, out var restored));
        Assert.Equal(apple, restored);
    }
}
