namespace SqlLogReader.Decoding;

public enum RecordType
{
    Primary = 0,
    Forwarded = 1,
    ForwardingStub = 2,
    Index = 3,
    BlobFragment = 4,
    GhostIndex = 5,
    GhostData = 6,
    GhostVersion = 7,
}

/// <summary>
/// Structure of a FixedVar row (data record or index record) as stored on a page and in the log.
/// </summary>
/// <remarks>
/// Data record: status A, status B, ushort offset to the column count, fixed-length data,
/// ushort column count, null bitmap, ushort variable column count, ushort end offsets
/// (high bit = complex/off-row column), variable-length data, optional 14-byte version tag.
/// Index records have a single status byte and no column-count offset; the fixed part
/// ends where the caller says it does.
/// </remarks>
public sealed class DataRecord
{
    private readonly byte[] _data;
    private readonly int _nullBitmapOffset;
    private readonly int _varOffsetsStart;
    private readonly int _varDataStart;

    private DataRecord(byte[] data, int fixedEnd, int columnCount, int nullBitmapOffset, int varCount, int varOffsetsStart)
    {
        _data = data;
        FixedEnd = fixedEnd;
        ColumnCount = columnCount;
        _nullBitmapOffset = nullBitmapOffset;
        VariableColumnCount = varCount;
        _varOffsetsStart = varOffsetsStart;
        _varDataStart = varOffsetsStart + varCount * 2;
    }

    public byte StatusA => _data[0];
    public RecordType Type => (RecordType)((StatusA >> 1) & 0x07);
    public bool HasNullBitmap => (StatusA & 0x10) != 0;
    public bool HasVariableColumns => (StatusA & 0x20) != 0;
    public bool HasVersionTag => (StatusA & 0x40) != 0;

    /// <summary>Offset of the first byte after the fixed-length data.</summary>
    public int FixedEnd { get; }
    public int ColumnCount { get; }
    public int VariableColumnCount { get; }
    public ReadOnlySpan<byte> Bytes => _data;
    public int Length => _data.Length;

    /// <summary>Parses a data (leaf) record: status bytes and the column-count offset come first.</summary>
    public static bool TryParseData(byte[] data, out DataRecord record)
    {
        record = null!;
        if (data.Length < 4) return false;
        var type = (RecordType)((data[0] >> 1) & 0x07);
        if (type is RecordType.ForwardingStub or RecordType.BlobFragment) return false;
        int fixedEnd = BitConverter.ToUInt16(data, 2);
        return TryParseTail(data, fixedEnd, out record);
    }

    /// <summary>Parses an index record (single status byte) whose fixed data ends at <paramref name="fixedEnd"/>.</summary>
    public static bool TryParseIndex(byte[] data, int fixedEnd, out DataRecord record)
    {
        record = null!;
        if (data.Length < 1 || fixedEnd > data.Length) return false;
        return TryParseTail(data, fixedEnd, out record);
    }

    private static bool TryParseTail(byte[] data, int fixedEnd, out DataRecord record)
    {
        record = null!;
        if (fixedEnd < 1 || fixedEnd > data.Length) return false;
        bool hasNullBitmap = (data[0] & 0x10) != 0;
        bool hasVar = (data[0] & 0x20) != 0;

        int pos = fixedEnd;
        int columnCount = 0;
        int nullBitmapOffset = -1;
        if (hasNullBitmap)
        {
            if (pos + 2 > data.Length) return false;
            columnCount = BitConverter.ToUInt16(data, pos);
            pos += 2;
            nullBitmapOffset = pos;
            pos += (columnCount + 7) / 8;
            if (pos > data.Length) return false;
        }

        int varCount = 0;
        int varOffsetsStart = pos;
        if (hasVar)
        {
            if (pos + 2 > data.Length) return false;
            varCount = BitConverter.ToUInt16(data, pos);
            pos += 2;
            varOffsetsStart = pos;
            if (pos + varCount * 2 > data.Length) return false;
        }

        record = new DataRecord(data, fixedEnd, columnCount, nullBitmapOffset, varCount, varOffsetsStart);
        return true;
    }

    /// <summary>True when the 1-based <paramref name="nullBit"/> is set in the null bitmap.</summary>
    public bool IsNull(int nullBit)
    {
        if (_nullBitmapOffset < 0 || nullBit <= 0) return false;
        int index = nullBit - 1;
        if (index >= ColumnCount) return false;
        return (_data[_nullBitmapOffset + index / 8] & (1 << (index % 8))) != 0;
    }

    /// <summary>Gets <paramref name="length"/> bytes of fixed data at <paramref name="offset"/>.</summary>
    public bool TryGetFixed(int offset, int length, out ReadOnlySpan<byte> value)
    {
        value = default;
        if (offset < 1 || length < 0 || offset + length > FixedEnd) return false;
        value = _data.AsSpan(offset, length);
        return true;
    }

    /// <summary>Gets the 1-based variable-length column <paramref name="index"/>.</summary>
    /// <param name="isComplex">True when the value is an off-row pointer (row overflow / LOB).</param>
    public bool TryGetVariable(int index, out ReadOnlySpan<byte> value, out bool isComplex)
    {
        value = default;
        isComplex = false;
        if (index < 1 || index > VariableColumnCount) return false;
        int start = index == 1 ? _varDataStart : RawEndOffset(index - 1) & 0x7FFF;
        int rawEnd = RawEndOffset(index);
        int end = rawEnd & 0x7FFF;
        isComplex = (rawEnd & 0x8000) != 0;
        if (start > end || end > _data.Length || start < _varDataStart) return false;
        value = _data.AsSpan(start, end - start);
        return true;
    }

    private int RawEndOffset(int index) => BitConverter.ToUInt16(_data, _varOffsetsStart + (index - 1) * 2);
}
