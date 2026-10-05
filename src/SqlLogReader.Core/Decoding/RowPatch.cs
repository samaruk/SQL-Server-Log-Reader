namespace SqlLogReader.Decoding;

/// <summary>One changed byte range of an in-place row update.</summary>
/// <param name="Offset">Offset of the range in the row image before the update.</param>
/// <param name="BeforeLength">Number of bytes replaced.</param>
/// <param name="Before">Bytes at the offset before the update; null when the record does not carry them (compensation records).</param>
/// <param name="After">Bytes that replaced them.</param>
public sealed record PatchRange(int Offset, int BeforeLength, byte[]? Before, byte[] After)
{
    public PatchRange(int offset, byte[] before, byte[] after) : this(offset, before.Length, before, after) { }
}

/// <summary>
/// The byte-level change described by an <c>LOP_MODIFY_ROW</c> or <c>LOP_MODIFY_COLUMNS</c> record.
/// Applying it to the row image before the update yields the image after it, and vice versa.
/// </summary>
public sealed class RowPatch
{
    public RowPatch(IReadOnlyList<PatchRange> ranges)
    {
        Ranges = ranges.OrderBy(r => r.Offset).ToArray();
    }

    public IReadOnlyList<PatchRange> Ranges { get; }

    /// <summary>False when some replaced bytes are unknown (compensation records), so the patch cannot be reverted.</summary>
    public bool CanRevert => Ranges.All(r => r.Before != null);

    /// <summary>
    /// <c>LOP_MODIFY_ROW</c>: element 0 = bytes before, element 1 = bytes after, at <c>[Offset in Row]</c>.
    /// Compensation records leave element 0 empty and give the replaced length in <c>[Modify Size]</c>.
    /// </summary>
    public static RowPatch FromModifyRow(int offsetInRow, int modifySize, byte[] before, byte[] after, bool isCompensation)
    {
        var range = isCompensation && before.Length == 0
            ? new PatchRange(offsetInRow, modifySize, null, after)
            : new PatchRange(offsetInRow, before, after);
        return new RowPatch(new[] { range });
    }

    /// <summary>
    /// <c>LOP_MODIFY_COLUMNS</c>: element 0 = (offset, offset) ushort pairs, element 1 = ushort replaced lengths,
    /// elements 2 and 3 = clustered key and lock information, then a before/after element pair per range.
    /// </summary>
    public static bool TryFromModifyColumns(IReadOnlyList<byte[]> elements, out RowPatch patch)
    {
        patch = null!;
        if (elements.Count < 4) return false;
        var offsets = elements[0];
        var lengths = elements[1];
        int rangeCount = lengths.Length / 2;
        if (rangeCount == 0 || offsets.Length < rangeCount * 4 || elements.Count < 4 + rangeCount * 2) return false;

        var ranges = new List<PatchRange>(rangeCount);
        for (int i = 0; i < rangeCount; i++)
        {
            int offset = BitConverter.ToUInt16(offsets, i * 4);
            int beforeLength = BitConverter.ToUInt16(lengths, i * 2);
            var before = elements[4 + i * 2];
            var after = elements[4 + i * 2 + 1];
            if (before.Length == beforeLength)
                ranges.Add(new PatchRange(offset, before, after));
            else if (before.Length == 0)
                ranges.Add(new PatchRange(offset, beforeLength, null, after)); // compensation record
            else
                return false;
        }

        var ordered = ranges.OrderBy(r => r.Offset).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].Offset < ordered[i - 1].Offset + ordered[i - 1].BeforeLength) return false;

        patch = new RowPatch(ordered);
        return true;
    }

    /// <summary>Produces the row image after the update. Returns false if <paramref name="before"/> does not match.</summary>
    public bool TryApply(byte[] before, out byte[] after)
    {
        after = null!;
        var result = new List<byte>(before.Length + 64);
        int pos = 0;
        foreach (var r in Ranges)
        {
            if (r.Offset < pos || r.Offset + r.BeforeLength > before.Length) return false;
            if (r.Before != null && !before.AsSpan(r.Offset, r.BeforeLength).SequenceEqual(r.Before)) return false;
            result.AddRange(before.AsSpan(pos, r.Offset - pos).ToArray());
            result.AddRange(r.After);
            pos = r.Offset + r.BeforeLength;
        }
        result.AddRange(before.AsSpan(pos).ToArray());
        after = result.ToArray();
        return true;
    }

    /// <summary>Produces the row image before the update. Returns false if <paramref name="after"/> does not match.</summary>
    public bool TryRevert(byte[] after, out byte[] before)
    {
        before = null!;
        if (!CanRevert) return false;
        var result = new List<byte>(after.Length + 64);
        int pos = 0;   // position in the after image
        int shift = 0; // after offset - before offset, accumulated over earlier ranges
        foreach (var r in Ranges)
        {
            int afterOffset = r.Offset + shift;
            if (afterOffset < pos || afterOffset + r.After.Length > after.Length) return false;
            if (!after.AsSpan(afterOffset, r.After.Length).SequenceEqual(r.After)) return false;
            result.AddRange(after.AsSpan(pos, afterOffset - pos).ToArray());
            result.AddRange(r.Before!);
            pos = afterOffset + r.After.Length;
            shift += r.After.Length - r.BeforeLength;
        }
        result.AddRange(after.AsSpan(pos).ToArray());
        before = result.ToArray();
        return true;
    }
}
