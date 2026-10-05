using System.Data.SqlTypes;
using System.Text;
using SqlLogReader.Schema;

namespace SqlLogReader.Decoding;

/// <summary>Converts the on-disk bytes of a column value into a .NET value.</summary>
public static class ValueDecoder
{
    private static readonly DateTime SqlEpoch = new(1900, 1, 1);

    static ValueDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Storage size of a fixed-length type, used when slicing index (key) records.</summary>
    public static int FixedStorageSize(ColumnInfo column) => column.SystemType switch
    {
        SqlSystemType.Bit => 1,
        _ => column.MaxLength,
    };

    /// <summary>Decodes a column value. Throws <see cref="NotSupportedException"/> for unsupported storage formats.</summary>
    public static object Decode(SqlSystemType type, ReadOnlySpan<byte> data, byte precision, byte scale, int codePage, short maxLength = 0)
    {
        switch (type)
        {
            case SqlSystemType.TinyInt:
                Require(data, 1, type);
                return data[0];
            case SqlSystemType.SmallInt:
                Require(data, 2, type);
                return BitConverter.ToInt16(data);
            case SqlSystemType.Int:
                Require(data, 4, type);
                return BitConverter.ToInt32(data);
            case SqlSystemType.BigInt:
                Require(data, 8, type);
                return BitConverter.ToInt64(data);
            case SqlSystemType.Bit:
                Require(data, 1, type);
                return (data[0] & 1) != 0;
            case SqlSystemType.Real:
                Require(data, 4, type);
                return BitConverter.ToSingle(data);
            case SqlSystemType.Float:
                Require(data, 8, type);
                return BitConverter.ToDouble(data);
            case SqlSystemType.Money:
                Require(data, 8, type);
                return DecodeMoney(data);
            case SqlSystemType.SmallMoney:
                Require(data, 4, type);
                return BitConverter.ToInt32(data) / 10000m;
            case SqlSystemType.Decimal:
            case SqlSystemType.Numeric:
                return DecodeDecimal(data, precision, scale);
            case SqlSystemType.DateTime:
                Require(data, 8, type);
                return DecodeDateTime(data);
            case SqlSystemType.SmallDateTime:
                Require(data, 4, type);
                return SqlEpoch.AddDays(BitConverter.ToUInt16(data.Slice(2, 2))).AddMinutes(BitConverter.ToUInt16(data));
            case SqlSystemType.Date:
                Require(data, 3, type);
                return DateOnly.FromDayNumber(ReadUInt24(data));
            case SqlSystemType.Time:
                return DecodeTime(data, scale);
            case SqlSystemType.DateTime2:
                {
                    int timeLength = TimeLength(scale);
                    Require(data, timeLength + 3, type);
                    var time = DecodeTime(data[..timeLength], scale);
                    return DateOnly.FromDayNumber(ReadUInt24(data.Slice(timeLength, 3))).ToDateTime(TimeOnly.MinValue).Add(time);
                }
            case SqlSystemType.DateTimeOffset:
                {
                    int timeLength = TimeLength(scale);
                    Require(data, timeLength + 5, type);
                    var time = DecodeTime(data[..timeLength], scale);
                    var utc = DateOnly.FromDayNumber(ReadUInt24(data.Slice(timeLength, 3))).ToDateTime(TimeOnly.MinValue).Add(time);
                    var offset = TimeSpan.FromMinutes(BitConverter.ToInt16(data.Slice(timeLength + 3, 2)));
                    return new DateTimeOffset(DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified), offset);
                }
            case SqlSystemType.UniqueIdentifier:
                Require(data, 16, type);
                return new Guid(data);
            case SqlSystemType.Char:
            case SqlSystemType.VarChar:
            case SqlSystemType.Text:
                return GetAnsiEncoding(codePage).GetString(data);
            case SqlSystemType.NChar:
            case SqlSystemType.NVarChar:
            case SqlSystemType.NText:
                return Encoding.Unicode.GetString(data);
            case SqlSystemType.Binary:
            case SqlSystemType.VarBinary:
            case SqlSystemType.Image:
            case SqlSystemType.Timestamp:
            case SqlSystemType.ClrType:
                return data.ToArray();
            case SqlSystemType.SqlVariant:
                return DecodeSqlVariant(data);
            case SqlSystemType.Xml:
                throw new NotSupportedException("xml values are stored in SQL Server's binary XML format.");
            default:
                throw new NotSupportedException($"Data type {(int)type} is not supported.");
        }
    }

    /// <summary>money: a little-endian 64-bit integer in units of 1/10000.</summary>
    public static decimal DecodeMoney(ReadOnlySpan<byte> data) => BitConverter.ToInt64(data[..8]) / 10000m;

    /// <summary>Decodes decimal/numeric: one sign byte (1 = positive) then a little-endian integer of 4, 8, 12 or 16 bytes.</summary>
    public static object DecodeDecimal(ReadOnlySpan<byte> data, byte precision, byte scale)
    {
        if (data.Length < 5 || (data.Length - 1) % 4 != 0)
            throw new InvalidDataException($"Invalid decimal storage length {data.Length}.");
        bool positive = data[0] == 1;
        var parts = new int[4];
        for (int i = 0; i < (data.Length - 1) / 4 && i < 4; i++)
            parts[i] = BitConverter.ToInt32(data.Slice(1 + i * 4, 4));
        if (precision == 0) precision = 38;
        var value = new SqlDecimal(precision, scale, positive, parts[0], parts[1], parts[2], parts[3]);
        try
        {
            return value.Value;
        }
        catch (OverflowException)
        {
            return value; // more than 28-29 significant digits: keep the exact SqlDecimal
        }
    }

    public static DateTime DecodeDateTime(ReadOnlySpan<byte> data)
    {
        // Time (1/300 second ticks since midnight) first, then days since 1900-01-01.
        int ticks300 = BitConverter.ToInt32(data[..4]);
        int days = BitConverter.ToInt32(data.Slice(4, 4));
        double milliseconds = Math.Round(ticks300 * 10.0 / 3.0, MidpointRounding.AwayFromZero);
        return SqlEpoch.AddDays(days).AddMilliseconds(milliseconds);
    }

    public static int TimeLength(byte scale) => scale <= 2 ? 3 : scale <= 4 ? 4 : 5;

    public static TimeSpan DecodeTime(ReadOnlySpan<byte> data, byte scale)
    {
        if (data.Length is < 3 or > 5) throw new InvalidDataException($"Invalid time storage length {data.Length}.");
        long units = 0;
        for (int i = data.Length - 1; i >= 0; i--)
            units = (units << 8) | data[i];
        long ticks = units;
        for (int i = scale; i < 7; i++) ticks *= 10;
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>Decodes a sql_variant: base type id, version, type-specific properties, then the value.</summary>
    public static object DecodeSqlVariant(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) throw new InvalidDataException("sql_variant value too short.");
        var baseType = (SqlSystemType)data[0];
        var rest = data[2..];
        byte precision = 0, scale = 0;
        int codePage = 1252;
        switch (baseType)
        {
            case SqlSystemType.Decimal:
            case SqlSystemType.Numeric:
                precision = rest[0];
                scale = rest[1];
                rest = rest[2..];
                break;
            case SqlSystemType.Time:
            case SqlSystemType.DateTime2:
            case SqlSystemType.DateTimeOffset:
                scale = rest[0];
                rest = rest[1..];
                break;
            case SqlSystemType.Char:
            case SqlSystemType.VarChar:
            case SqlSystemType.NChar:
            case SqlSystemType.NVarChar:
                // max length (2 bytes) + collation id (4 bytes, COLLATIONPROPERTY(name, 'CollationId'))
                codePage = CollationCodePages.Get(BitConverter.ToInt32(rest.Slice(2, 4)));
                rest = rest[6..];
                break;
            case SqlSystemType.Binary:
            case SqlSystemType.VarBinary:
                rest = rest[2..];
                break;
        }
        return new SqlVariantValue(baseType, Decode(baseType, rest, precision, scale, codePage), precision, scale);
    }

    public static Encoding GetAnsiEncoding(int codePage)
    {
        if (codePage <= 0) codePage = 1252;
        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data) => data[0] | (data[1] << 8) | (data[2] << 16);

    private static void Require(ReadOnlySpan<byte> data, int length, SqlSystemType type)
    {
        if (data.Length < length)
            throw new InvalidDataException($"{type} value needs {length} bytes but only {data.Length} are available.");
    }
}

/// <summary>A decoded sql_variant value together with its base type.</summary>
public sealed record SqlVariantValue(SqlSystemType BaseType, object Value, byte Precision, byte Scale)
{
    public override string ToString() => Value is byte[] b ? "0x" + Convert.ToHexString(b) : Convert.ToString(Value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
}
