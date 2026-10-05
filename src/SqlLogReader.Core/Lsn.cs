using System.Globalization;

namespace SqlLogReader;

/// <summary>
/// A SQL Server log sequence number: virtual log file sequence, log block offset
/// (in 512-byte units from the start of the VLF) and the slot of the record in the block.
/// Rendered the same way <c>fn_dblog</c> prints it, e.g. <c>0000002A:00000168:001C</c>.
/// </summary>
public readonly record struct Lsn(uint VlfSequence, uint BlockOffset, ushort Slot) : IComparable<Lsn>
{
    public static readonly Lsn Zero = new(0, 0, 0);

    public bool IsZero => VlfSequence == 0 && BlockOffset == 0 && Slot == 0;

    public int CompareTo(Lsn other)
    {
        int c = VlfSequence.CompareTo(other.VlfSequence);
        if (c != 0) return c;
        c = BlockOffset.CompareTo(other.BlockOffset);
        return c != 0 ? c : Slot.CompareTo(other.Slot);
    }

    public static bool operator <(Lsn a, Lsn b) => a.CompareTo(b) < 0;
    public static bool operator >(Lsn a, Lsn b) => a.CompareTo(b) > 0;
    public static bool operator <=(Lsn a, Lsn b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Lsn a, Lsn b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{VlfSequence:X8}:{BlockOffset:X8}:{Slot:X4}";

    /// <summary>The decimal form used by backup history tables (e.g. <c>msdb.dbo.backupset.first_lsn</c>).</summary>
    public decimal ToDecimal() => VlfSequence * 1_000_000_000_000_000m + BlockOffset * 100_000m + Slot;

    /// <summary>Converts the decimal form used by msdb (e.g. <c>42000000026400001</c>).</summary>
    public static Lsn FromDecimal(decimal value)
    {
        var vlf = decimal.Truncate(value / 1_000_000_000_000_000m);
        var rest = value - vlf * 1_000_000_000_000_000m;
        var block = decimal.Truncate(rest / 100_000m);
        var slot = rest - block * 100_000m;
        return new Lsn((uint)vlf, (uint)block, (ushort)slot);
    }

    /// <summary>Parses either the <c>fn_dblog</c> form or the msdb decimal form.</summary>
    public static bool TryParseAny(string? text, out Lsn lsn)
    {
        if (TryParse(text, out lsn)) return true;
        if (decimal.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d >= 0)
        {
            lsn = FromDecimal(d);
            return true;
        }
        return false;
    }

    /// <summary>Parses the <c>fn_dblog</c> form (<c>0000002A:00000168:001C</c>, optionally prefixed with 0x).</summary>
    public static Lsn Parse(string text)
    {
        if (!TryParse(text, out var lsn))
            throw new FormatException($"'{text}' is not a valid LSN. Expected the form 0000002A:00000168:001C.");
        return lsn;
    }

    public static bool TryParse(string? text, out Lsn lsn)
    {
        lsn = Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        var parts = s.Split(':');
        if (parts.Length != 3) return false;
        if (!uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vlf)) return false;
        if (!uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var block)) return false;
        if (!ushort.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var slot)) return false;
        lsn = new Lsn(vlf, block, slot);
        return true;
    }

    /// <summary>Reads the 10-byte on-disk form (VLF sequence, block offset, slot; little endian).</summary>
    public static Lsn FromBytes(ReadOnlySpan<byte> bytes) =>
        new(BitConverter.ToUInt32(bytes[..4]), BitConverter.ToUInt32(bytes.Slice(4, 4)), BitConverter.ToUInt16(bytes.Slice(8, 2)));
}
