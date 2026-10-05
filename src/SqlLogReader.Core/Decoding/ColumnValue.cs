using System.Data.SqlTypes;
using System.Globalization;
using SqlLogReader.Schema;

namespace SqlLogReader.Decoding;

public enum ValueState
{
    /// <summary>A decoded value.</summary>
    Value,
    /// <summary>SQL NULL.</summary>
    Null,
    /// <summary>The value is stored off-row (LOB / row-overflow) or is otherwise not contained in the log record.</summary>
    Unavailable,
    /// <summary>The bytes could not be decoded.</summary>
    Error,
}

/// <summary>One decoded column of a row image.</summary>
public sealed class ColumnValue
{
    public required ColumnInfo Column { get; init; }
    public ValueState State { get; init; }
    public object? Value { get; init; }

    /// <summary>The stored bytes (bit columns: a single 0/1 byte). Used to detect changed columns.</summary>
    public byte[]? RawBytes { get; init; }

    /// <summary>Explanation for <see cref="ValueState.Unavailable"/> / <see cref="ValueState.Error"/>.</summary>
    public string? Note { get; init; }

    public string Name => Column.Name;
    public bool IsNull => State == ValueState.Null;
    public bool HasValue => State is ValueState.Value or ValueState.Null;

    public static ColumnValue Null(ColumnInfo column) => new() { Column = column, State = ValueState.Null };

    public static ColumnValue Unavailable(ColumnInfo column, string note, byte[]? raw = null) =>
        new() { Column = column, State = ValueState.Unavailable, Note = note, RawBytes = raw };

    /// <summary>True when both values are known and stored identically.</summary>
    public bool SameAs(ColumnValue other)
    {
        if (State != other.State) return false;
        if (State == ValueState.Null) return true;
        if (RawBytes == null || other.RawBytes == null) return false;
        return RawBytes.AsSpan().SequenceEqual(other.RawBytes);
    }

    /// <summary>Human readable rendering of the value.</summary>
    public string DisplayText => State switch
    {
        ValueState.Null => "NULL",
        ValueState.Unavailable => $"<{Note ?? "not available"}>",
        ValueState.Error => $"<error: {Note}>",
        _ => FormatValue(Value),
    };

    public static string FormatValue(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        bool b => b ? "1" : "0",
        DateTime dt => dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        SqlDecimal sd => sd.ToString(),
        Guid g => g.ToString().ToUpperInvariant(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    public override string ToString() => $"{Name} = {DisplayText}";
}
