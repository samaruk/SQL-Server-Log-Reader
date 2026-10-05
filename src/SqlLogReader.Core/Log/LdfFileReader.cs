using System.Buffers.Binary;
using System.Text;
using SqlLogReader.Decoding;

namespace SqlLogReader.Log;

/// <summary>A virtual log file found in an .ldf file.</summary>
public sealed record VirtualLogFileInfo(long Offset, long Size, uint Sequence, byte Parity, int Blocks, int Records)
{
    /// <summary>The log file the VLF belongs to.</summary>
    public string? FilePath { get; init; }
}

public sealed class LdfReadResult
{
    public required string FilePath { get; init; }
    public required IReadOnlyList<LogRecord> Records { get; init; }
    public required IReadOnlyList<VirtualLogFileInfo> VirtualLogFiles { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Reads log records straight from a transaction log file (.ldf), without SQL Server.
/// </summary>
/// <remarks>
/// File layout:
/// <list type="bullet">
/// <item>8 KB file header page, then the virtual log files (VLFs) back to back.</item>
/// <item>VLF header (first 8 KB of the VLF): 0xAB, parity (0x40/0x80; 0 = never used), ushort, uint sequence,
/// 8 reserved bytes, long size at 16, long file offset at 24.</item>
/// <item>Log blocks follow at VLF offset 8 KB, each a multiple of 512 bytes. Header: flags/parity (byte 0),
/// ushort record count (2), ushort end of the slot array (4), ushort block size (6), uint VLF sequence (12),
/// uint block offset in 512-byte units (16), ushort first slot (20). Slot k's record offset is the ushort
/// at <c>slotEnd - 2k</c>.</item>
/// <item>The first byte of every 512-byte sector is overwritten with parity bits; the original byte of
/// sector i is saved at <c>blockSize - 1 - i</c>.</item>
/// </list>
/// An LSN is (VLF sequence, block offset, slot), so records come out in the same form as fn_dblog shows them.
/// </remarks>
public static class LdfFileReader
{
    /// <summary>
    /// Reads several log files of the same database (a database can have more than one log file)
    /// and merges their records in LSN order.
    /// </summary>
    public static LdfReadResult ReadFiles(IReadOnlyList<string> paths, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 1) return Read(paths[0], progress, cancellationToken);
        var records = new List<LogRecord>();
        var vlfs = new List<VirtualLogFileInfo>();
        var warnings = new List<string>();
        foreach (var path in paths)
        {
            var result = Read(path, progress, cancellationToken);
            records.AddRange(result.Records);
            vlfs.AddRange(result.VirtualLogFiles);
            warnings.AddRange(result.Warnings.Select(w => $"{Path.GetFileName(path)}: {w}"));
        }
        records.Sort((a, b) => a.Lsn.CompareTo(b.Lsn));
        return new LdfReadResult { FilePath = string.Join("; ", paths), Records = records, VirtualLogFiles = vlfs, Warnings = warnings };
    }

    private const int PageSize = 8192;
    private const int SectorSize = 512;
    private const int MaxBlockSize = 60 * 1024;
    private static readonly DateTime SqlEpoch = new(1900, 1, 1);

    public static LdfReadResult Read(string path, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var records = new List<LogRecord>();
        var vlfs = new List<VirtualLogFileInfo>();
        var warnings = new List<string>();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        long length = stream.Length;
        if (length < 2 * PageSize)
            throw new InvalidDataException($"'{path}' is too small to be a SQL Server log file.");

        var header = ReadExactly(stream, 0, 2);
        if (header[0] != 0x01 || header[1] != 0x0F)
            warnings.Add("The file does not start with a SQL Server file header page; continuing anyway.");

        var vlfHeader = new byte[32];
        var block = new byte[MaxBlockSize];
        long offset = PageSize;
        while (offset + PageSize <= length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadInto(stream, offset, vlfHeader, 32);
            if (vlfHeader[0] != 0xAB)
            {
                warnings.Add($"No virtual log file header at offset {offset}; stopped reading.");
                break;
            }
            byte parity = vlfHeader[1];
            uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(vlfHeader.AsSpan(4));
            long size = BinaryPrimitives.ReadInt64LittleEndian(vlfHeader.AsSpan(16));
            long start = BinaryPrimitives.ReadInt64LittleEndian(vlfHeader.AsSpan(24));
            if (size < 2 * PageSize || offset + size > length || start != offset)
            {
                warnings.Add($"Invalid virtual log file header at offset {offset}; stopped reading.");
                break;
            }

            int blocks = 0, count = 0;
            if (sequence != 0 && parity is 0x40 or 0x80)
            {
                progress?.Report($"Reading virtual log file {sequence} at offset {offset:N0}...");
                (blocks, count) = ReadVlf(stream, offset, size, sequence, parity, block, records, cancellationToken);
            }
            vlfs.Add(new VirtualLogFileInfo(offset, size, sequence, parity, blocks, count) { FilePath = path });
            offset += size;
        }

        records.Sort((a, b) => a.Lsn.CompareTo(b.Lsn));
        return new LdfReadResult { FilePath = path, Records = records, VirtualLogFiles = vlfs, Warnings = warnings };
    }

    private static (int Blocks, int Records) ReadVlf(FileStream stream, long vlfOffset, long vlfSize, uint sequence, byte parity,
        byte[] block, List<LogRecord> records, CancellationToken ct)
    {
        int blocks = 0, count = 0;
        long position = vlfOffset + PageSize;
        long end = vlfOffset + vlfSize;
        var head = new byte[24];
        while (position + SectorSize <= end)
        {
            ct.ThrowIfCancellationRequested();
            ReadInto(stream, position, head, head.Length);
            int recordCount = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
            int slotEnd = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(4));
            int size = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(6));
            uint blockSequence = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
            uint blockOffset = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16));
            int firstSlot = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(20));

            // A block of the current VLF generation carries the VLF's parity and identifies itself;
            // anything else is unused space or data of an older generation.
            bool valid = (head[0] & 0xC0) == parity
                         && size >= SectorSize && size <= MaxBlockSize && size % SectorSize == 0
                         && position + size <= end
                         && blockSequence == sequence
                         && blockOffset == (position - vlfOffset) / SectorSize
                         && recordCount > 0 && slotEnd <= size && slotEnd >= 24 + 2 * recordCount;
            if (!valid) break;

            ReadInto(stream, position, block, size);
            int sectors = size / SectorSize;
            bool torn = false;
            for (int i = 1; i < sectors; i++)
            {
                if ((block[i * SectorSize] & 0xC0) != parity) torn = true;
                block[i * SectorSize] = block[size - 1 - i];
            }
            if (torn) break; // partially written block

            int arrayStart = slotEnd - 2 * recordCount;
            for (int k = 1; k <= recordCount; k++)
            {
                int recordOffset = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(slotEnd - 2 * k));
                int recordEnd = k < recordCount ? BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(slotEnd - 2 * (k + 1))) : arrayStart;
                if (recordOffset < 24 || recordEnd <= recordOffset || recordEnd > arrayStart) continue;
                var lsn = new Lsn(sequence, blockOffset, (ushort)(firstSlot + k - 1));
                var record = ParseRecord(lsn, block.AsSpan(recordOffset, recordEnd - recordOffset));
                if (record != null)
                {
                    records.Add(record);
                    count++;
                }
            }

            blocks++;
            position += size;
        }
        return (blocks, count);
    }

    /// <summary>Builds a <see cref="LogRecord"/> from the binary record, filling the same fields fn_dblog shows.</summary>
    public static LogRecord? ParseRecord(Lsn lsn, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24) return null;
        int fixedLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        if (fixedLength < 24 || fixedLength > bytes.Length) return null;

        var raw = bytes.ToArray();

        byte op = raw[22];
        byte ctx = raw[23];
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(14));
        uint xactLow = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(16));
        ushort xactHigh = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(20));
        string operation = LogOperationCodes.OperationName(op);
        bool isCompensation = (flags & 0x0001) != 0;

        string? pageId = null;
        int? slot = null, offsetInRow = null, modifySize = null;
        long? allocUnitId = null;
        if (LogOperationCodes.IsRowOperation(op) && fixedLength >= 62)
        {
            uint page = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(24));
            ushort file = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(28));
            pageId = $"{file:x4}:{page:x8}";
            slot = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(30));
            // Allocation unit id = (m_indexId << 48) | (m_objId << 16); bytes 48..55 hold the partition (HoBt) id.
            uint auObject = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(32));
            ushort auIndex = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(46));
            allocUnitId = ((long)auIndex << 48) | ((long)auObject << 16);
            offsetInRow = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(56));
            modifySize = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(58));
        }

        string? name = null;
        byte[]? sid = null;
        DateTime? beginTime = null, endTime = null;
        if (op == 0x80 && fixedLength >= 48) // LOP_BEGIN_XACT
        {
            beginTime = ReadDateTime(raw.AsSpan(40));
            if (LogRecordPayload.TryParseElements(raw, out var elements))
            {
                if (elements.Length > 0) name = Encoding.Unicode.GetString(elements[0]).TrimEnd('\0');
                if (elements.Length > 1 && elements[1].Length > 0) sid = elements[1];
            }
        }
        else if (op is 0x81 or 0x82 && fixedLength >= 32) // LOP_COMMIT_XACT / LOP_ABORT_XACT
        {
            endTime = ReadDateTime(raw.AsSpan(24));
        }

        return new LogRecord
        {
            Lsn = lsn,
            PreviousLsn = Lsn.FromBytes(raw.AsSpan(4, 10)),
            Operation = operation,
            Context = LogOperationCodes.ContextName(ctx),
            TransactionId = $"{xactHigh:x4}:{xactLow:x8}",
            AllocUnitId = allocUnitId,
            PageId = pageId,
            SlotId = slot,
            OffsetInRow = offsetInRow,
            ModifySize = modifySize,
            TransactionName = name,
            BeginTime = beginTime,
            EndTime = endTime,
            TransactionSid = sid,
            Description = isCompensation ? "COMPENSATION" : null,
            RecordBytes = raw,
            Source = "ldf",
        };
    }

    /// <summary>A datetime value: 1/300-second ticks since midnight, then days since 1900-01-01.</summary>
    private static DateTime? ReadDateTime(ReadOnlySpan<byte> bytes)
    {
        int ticks = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        int days = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        if (ticks < 0 || ticks > 300 * 86400 || days <= 0 || days > 2_958_463) return null;
        return ValueDecoder.DecodeDateTime(bytes[..8]);
    }

    private static byte[] ReadExactly(FileStream stream, long offset, int count)
    {
        var buffer = new byte[count];
        ReadInto(stream, offset, buffer, count);
        return buffer;
    }

    private static void ReadInto(FileStream stream, long offset, byte[] buffer, int count)
    {
        stream.Position = offset;
        stream.ReadExactly(buffer, 0, count);
    }
}
