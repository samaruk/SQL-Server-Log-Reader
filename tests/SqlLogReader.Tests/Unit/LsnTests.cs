namespace SqlLogReader.Tests.Unit;

public class LsnTests
{
    [Fact]
    public void Parses_and_formats_fn_dblog_form()
    {
        var lsn = Lsn.Parse("0000002A:00000168:001C");
        Assert.Equal(0x2Au, lsn.VlfSequence);
        Assert.Equal(0x168u, lsn.BlockOffset);
        Assert.Equal((ushort)0x1C, lsn.Slot);
        Assert.Equal("0000002A:00000168:001C", lsn.ToString());
        Assert.Equal(lsn, Lsn.Parse("0x0000002a:00000168:001c"));
    }

    [Fact]
    public void Orders_by_vlf_then_block_then_slot()
    {
        var a = Lsn.Parse("0000002A:00000168:001C");
        var b = Lsn.Parse("0000002A:00000168:001D");
        var c = Lsn.Parse("0000002A:00000170:0001");
        var d = Lsn.Parse("0000002B:00000010:0001");
        Assert.True(a < b && b < c && c < d);
        Assert.Equal(new[] { a, b, c, d }, new[] { d, b, c, a }.OrderBy(x => x));
    }

    [Fact]
    public void Converts_msdb_decimal_form()
    {
        var lsn = Lsn.FromDecimal(42000000026400001m);
        Assert.Equal("0000002A:00000108:0001", lsn.ToString());
        Assert.Equal(42000000026400001m, lsn.ToDecimal());
        Assert.True(Lsn.TryParseAny("42000000026400001", out var parsed));
        Assert.Equal(lsn, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000002A:00000168")]
    [InlineData("zz:00000168:0001")]
    public void Rejects_invalid_text(string text) => Assert.False(Lsn.TryParse(text, out _));
}
