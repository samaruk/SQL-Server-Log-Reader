using System.Data.SqlTypes;
using SqlLogReader.Decoding;

namespace SqlLogReader.Tests.Integration;

/// <summary>Compares decoded log values with values read through SqlClient.</summary>
internal static class Comparison
{
    public static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        SqlVariantValue v => Normalize(v.Value),
        SqlDecimal sd => sd.ToString(),
        _ => value,
    };

    public static void AssertSameValue(object? expected, ColumnValue actual, string context)
    {
        var exp = Normalize(expected);
        if (exp == null)
        {
            Assert.True(actual.IsNull, $"{context}: expected NULL but decoded {actual.DisplayText}");
            return;
        }
        Assert.True(actual.State == ValueState.Value, $"{context}: expected {exp} but decoded {actual.DisplayText}");
        var act = Normalize(actual.Value);
        if (exp is byte[] eb)
            Assert.Equal(eb, Assert.IsType<byte[]>(act));
        else
            Assert.True(Equals(exp, act), $"{context}: expected '{exp}' ({exp.GetType().Name}) but decoded '{act}' ({act?.GetType().Name})");
    }
}
