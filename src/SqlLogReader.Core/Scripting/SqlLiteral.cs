using System.Data.SqlTypes;
using System.Globalization;
using SqlLogReader.Decoding;
using SqlLogReader.Schema;

namespace SqlLogReader.Scripting;

/// <summary>Formats decoded values as T-SQL literals.</summary>
public static class SqlLiteral
{
    /// <summary>Returns the literal, or null when the value is not known (off-row / undecodable).</summary>
    public static string? TryFormat(ColumnValue value)
    {
        return value.State switch
        {
            ValueState.Null => "NULL",
            ValueState.Value => Format(value.Column, value.Value),
            _ => null,
        };
    }

    public static string Format(ColumnInfo column, object? value)
    {
        if (value == null) return "NULL";
        if (column.SystemType == SqlSystemType.ClrType && value is byte[] clr)
            return $"CAST({Binary(clr)} AS {column.TypeName})";
        return FormatValue(column.SystemType, value, column.Scale);
    }

    public static string FormatValue(SqlSystemType type, object value, byte scale = 7)
    {
        switch (value)
        {
            case null:
                return "NULL";
            case SqlVariantValue variant:
                return $"CAST({FormatValue(variant.BaseType, variant.Value, variant.Scale)} AS {VariantTypeName(variant)})";
            case bool b:
                return b ? "1" : "0";
            case byte[] bytes:
                return Binary(bytes);
            case string s:
                return String(s, unicode: type is SqlSystemType.NChar or SqlSystemType.NVarChar or SqlSystemType.NText);
            case Guid g:
                return $"'{g.ToString().ToUpperInvariant()}'";
            case DateOnly d:
                return $"'{d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'";
            case TimeSpan t:
                return $"'{FormatTime(t, scale)}'";
            case DateTimeOffset dto:
                return $"'{dto.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}{Fraction(dto.Ticks % TimeSpan.TicksPerSecond, scale)}{dto.ToString("zzz", CultureInfo.InvariantCulture)}'";
            case DateTime dt:
                return type switch
                {
                    SqlSystemType.SmallDateTime => $"'{dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}'",
                    SqlSystemType.DateTime => $"'{dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)}'",
                    _ => $"'{dt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}{Fraction(dt.Ticks % TimeSpan.TicksPerSecond, scale)}'",
                };
            case double d:
                return d.ToString("R", CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("R", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString(CultureInfo.InvariantCulture);
            case SqlDecimal sd:
                return sd.ToString();
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            default:
                return String(value.ToString() ?? "", unicode: true);
        }
    }

    public static string String(string s, bool unicode)
    {
        bool needsUnicode = unicode || s.Any(c => c > 127);
        return (needsUnicode ? "N'" : "'") + s.Replace("'", "''") + "'";
    }

    public static string Binary(byte[] bytes) => bytes.Length == 0 ? "0x" : "0x" + Convert.ToHexString(bytes);

    private static string FormatTime(TimeSpan t, byte scale) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{Fraction(t.Ticks % TimeSpan.TicksPerSecond, scale)}";

    private static string Fraction(long ticks, byte scale)
    {
        if (scale == 0) return "";
        var digits = ticks.ToString("0000000", CultureInfo.InvariantCulture);
        return "." + digits[..Math.Min((int)scale, 7)];
    }

    private static string VariantTypeName(SqlVariantValue v) => v.BaseType switch
    {
        SqlSystemType.Decimal => $"decimal({v.Precision},{v.Scale})",
        SqlSystemType.Numeric => $"numeric({v.Precision},{v.Scale})",
        SqlSystemType.Time => $"time({v.Scale})",
        SqlSystemType.DateTime2 => $"datetime2({v.Scale})",
        SqlSystemType.DateTimeOffset => $"datetimeoffset({v.Scale})",
        SqlSystemType.Char or SqlSystemType.VarChar or SqlSystemType.Binary or SqlSystemType.VarBinary =>
            $"{v.BaseType.ToString().ToLowerInvariant()}({Math.Max(1, LengthOf(v.Value))})",
        SqlSystemType.NChar or SqlSystemType.NVarChar => $"{v.BaseType.ToString().ToLowerInvariant()}({Math.Max(1, LengthOf(v.Value))})",
        SqlSystemType.UniqueIdentifier => "uniqueidentifier",
        _ => v.BaseType.ToString().ToLowerInvariant(),
    };

    private static int LengthOf(object value) => value switch
    {
        string s => s.Length,
        byte[] b => b.Length,
        _ => 1,
    };
}
