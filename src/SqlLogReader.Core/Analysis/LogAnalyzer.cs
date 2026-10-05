using SqlLogReader.Decoding;
using SqlLogReader.Log;
using SqlLogReader.Schema;

namespace SqlLogReader.Analysis;

/// <summary>Result of analysing a set of log records.</summary>
public sealed class LogAnalysisResult
{
    public required IReadOnlyList<RowChange> Changes { get; init; }
    public required IReadOnlyList<TransactionInfo> Transactions { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public int RecordCount { get; init; }
    public Lsn? FirstLsn { get; init; }
    public Lsn? LastLsn { get; init; }
}

/// <summary>
/// Turns raw log records into decoded row changes: groups the records into transactions,
/// decodes inserted/deleted rows, and rebuilds before/after images of updates by chaining every
/// row's history (insert → updates → delete), optionally anchored on the row's current bytes.
/// </summary>
public sealed class LogAnalyzer
{
    public const string BeginXact = "LOP_BEGIN_XACT";
    public const string CommitXact = "LOP_COMMIT_XACT";
    public const string AbortXact = "LOP_ABORT_XACT";
    public const string InsertRows = "LOP_INSERT_ROWS";
    public const string DeleteRows = "LOP_DELETE_ROWS";
    public const string ModifyRow = "LOP_MODIFY_ROW";
    public const string ModifyColumns = "LOP_MODIFY_COLUMNS";

    public static readonly IReadOnlySet<string> RowOperations =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { InsertRows, DeleteRows, ModifyRow, ModifyColumns };

    public static readonly IReadOnlySet<string> TransactionOperations =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BeginXact, CommitXact, AbortXact };

    public static readonly IReadOnlySet<string> DataContexts =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LCX_HEAP", "LCX_CLUSTERED", "LCX_MARK_AS_GHOST" };

    private readonly DatabaseSchema _schema;
    private readonly ICurrentRowSource? _currentRows;

    public LogAnalyzer(DatabaseSchema schema, ICurrentRowSource? currentRows = null)
    {
        _schema = schema;
        _currentRows = currentRows;
    }

    public async Task<LogAnalysisResult> AnalyzeAsync(
        IReadOnlyList<LogRecord> records,
        AnalyzerOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AnalyzerOptions();
        var warnings = new List<string>();
        var transactions = CollectTransactions(records);

        progress?.Report("Decoding row operations...");
        var ops = CollectRowOperations(records, transactions, warnings);
        PairCompensations(ops, warnings);

        progress?.Report("Reconstructing row images...");
        var histories = ops.Where(o => o.Identity != null)
            .GroupBy(o => (o.Table.AllocationUnitId, o.Identity!))
            .Select(g => new RowHistory(g.OrderBy(o => o.Index).ToList()))
            .ToList();
        foreach (var history in histories)
            history.Propagate();

        if (options.UseCurrentData && _currentRows != null)
            await AnchorOnCurrentDataAsync(histories, options, warnings, progress, cancellationToken);

        foreach (var history in histories)
            history.Publish();

        progress?.Report("Building change list...");
        foreach (var op in ops.Where(o => !o.IsCompensation && o.Decodable && !o.IsInternal))
        {
            op.Transaction.ChangeCount++;
            op.Transaction.Tables.Add(op.Table.DisplayName);
        }

        var changes = new List<RowChange>();
        foreach (var op in ops.Where(o => !o.IsCompensation && o.Decodable && !o.IsInternal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Matches(op, options)) continue;
            var change = BuildChange(op);
            if (change != null)
                changes.Add(change);
        }

        var txList = transactions.Values
            .Where(t => options.IncludeAllTransactions || t.ChangeCount > 0 || IsDdlTransaction(t))
            .Where(t => TransactionMatches(t, options))
            .Where(t => options.Tables.Count == 0 || t.Tables.Any(name => options.Tables.Any(f => MatchesTableName(name, f))))
            .OrderBy(t => t.BeginLsn ?? t.EndLsn ?? Lsn.Zero)
            .ToList();

        return new LogAnalysisResult
        {
            Changes = changes,
            Transactions = txList,
            Warnings = warnings,
            RecordCount = records.Count,
            FirstLsn = records.Count > 0 ? records[0].Lsn : null,
            LastLsn = records.Count > 0 ? records[^1].Lsn : null,
        };
    }

    // ---------------------------------------------------------------- transactions

    private static Dictionary<string, TransactionInfo> CollectTransactions(IReadOnlyList<LogRecord> records)
    {
        var result = new Dictionary<string, TransactionInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            if (string.IsNullOrEmpty(r.TransactionId) || r.TransactionId == "0000:00000000") continue;
            if (!result.TryGetValue(r.TransactionId, out var tx))
                result[r.TransactionId] = tx = new TransactionInfo { TransactionId = r.TransactionId };

            switch (r.Operation)
            {
                case BeginXact:
                    tx.Name = r.TransactionName;
                    tx.BeginTime = r.BeginTime;
                    tx.BeginLsn = r.Lsn;
                    tx.UserSid = r.TransactionSid;
                    tx.UserName = r.UserName;
                    break;
                case CommitXact:
                    tx.EndTime = r.EndTime;
                    tx.EndLsn = r.Lsn;
                    tx.State = TransactionState.Committed;
                    break;
                case AbortXact:
                    tx.EndTime = r.EndTime;
                    tx.EndLsn = r.Lsn;
                    tx.State = TransactionState.RolledBack;
                    break;
            }
        }
        return result;
    }

    // ---------------------------------------------------------------- row operations

    private List<RowOp> CollectRowOperations(IReadOnlyList<LogRecord> records, Dictionary<string, TransactionInfo> transactions, List<string> warnings)
    {
        var ops = new List<RowOp>();
        int undecodable = 0;
        var unsupportedTables = new HashSet<string>();
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (!RowOperations.Contains(r.Operation)) continue;
            if (r.Context == null || !DataContexts.Contains(r.Context)) continue;
            if (r.AllocUnitId is not long au || !_schema.TryGetTable(au, out var table)) continue;
            if (!transactions.TryGetValue(r.TransactionId, out var tx)) continue;
            if (IsInternalMove(r, tx)) continue;

            if (table.IsCompressed)
            {
                if (unsupportedTables.Add(table.DisplayName))
                    warnings.Add($"{table.DisplayName} uses data compression; its rows cannot be decoded.");
                continue;
            }

            var op = CreateOperation(i, r, table, tx);
            if (op == null) continue;
            ops.Add(op);
        }

        MarkHeapForwarding(ops);
        undecodable = ops.Count(o => !o.Decodable && !o.IsCompensation && !o.IsInternal);
        if (undecodable > 0)
            warnings.Add($"{undecodable} row operation(s) could not be decoded.");
        return ops;
    }

    /// <summary>Rows moved by page splits are logged as inserts inside a system transaction.</summary>
    private static bool IsInternalMove(LogRecord r, TransactionInfo tx)
    {
        if (r.Description != null && r.Description.StartsWith("Inserted ", StringComparison.OrdinalIgnoreCase)
            && r.Description.Contains("rows on page", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = tx.Name ?? "";
        return name.StartsWith("Split", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("BTreeMgr", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A heap row that no longer fits on its page is moved: SQL Server inserts a forwarded copy elsewhere,
    /// updates the copy, and turns the original slot into a forwarding stub. Those records are internal
    /// moves; only the update of the forwarded copy is reported.
    /// </summary>
    private static void MarkHeapForwarding(List<RowOp> ops)
    {
        static bool IsStub(byte status) => (RecordType)((status >> 1) & 0x07) == RecordType.ForwardingStub;

        var stubTransactions = new HashSet<(TransactionInfo, long)>();
        foreach (var op in ops.Where(o => o.Table.IsHeap))
        {
            if (op.Kind == ChangeOperation.Update && op.Patch != null &&
                op.Patch.Ranges.Any(r => r.Offset == 0 && ((r.After.Length > 0 && IsStub(r.After[0])) || (r.Before is { Length: > 0 } b && IsStub(b[0])))))
            {
                op.IsInternal = true;
                stubTransactions.Add((op.Transaction, op.Table.AllocationUnitId));
            }
            else if (op.RecordType == RecordType.ForwardingStub || (op.Kind == ChangeOperation.Insert && op.RecordType == RecordType.Forwarded))
            {
                op.IsInternal = true;
            }
        }

        // Moving an already forwarded row again deletes the old forwarded copy and re-points the stub.
        foreach (var op in ops.Where(o => o.Kind == ChangeOperation.Delete && o.RecordType == RecordType.Forwarded))
            if (stubTransactions.Contains((op.Transaction, op.Table.AllocationUnitId)))
                op.IsInternal = true;
    }

    private static RowOp? CreateOperation(int index, LogRecord r, TableInfo table, TransactionInfo tx)
    {
        var elements = r.Elements;
        var op = new RowOp
        {
            Index = index,
            Record = r,
            Table = table,
            Transaction = tx,
            Kind = r.Operation switch
            {
                InsertRows => ChangeOperation.Insert,
                DeleteRows => ChangeOperation.Delete,
                _ => ChangeOperation.Update,
            },
            IsCompensation = r.IsCompensation,
        };

        if (op.Kind != ChangeOperation.Update)
        {
            if (elements.Count == 0 || elements[0].Length == 0)
            {
                op.Problem = "the log record does not contain the row image";
                op.Identity = table.IsHeap ? RowIdentity.ForHeap(r) : null;
                return op;
            }

            op.Row = elements[0];
            if (op.Kind == ChangeOperation.Insert) op.AfterImage = op.Row;
            else op.BeforeImage = op.Row;
            op.RecordType = (RecordType)((op.Row[0] >> 1) & 0x07);

            if (RowDecoder.TryDecode(table, op.Row, out var decoded, out var error))
            {
                op.Identity = RowIdentity.ForRow(table, r, decoded);
            }
            else
            {
                op.Problem = error;
                op.Identity = table.IsHeap ? RowIdentity.ForHeap(r) : null;
            }
            return op;
        }

        // Updates.
        if (r.Operation == ModifyRow)
        {
            if (elements.Count < 2)
            {
                op.Problem = "the log record does not contain the modified bytes";
            }
            else
            {
                int offset = r.OffsetInRow ?? ReadUInt16(r.RecordBytes, LogRecordPayload.OffsetInRowOffset) ?? 0;
                int size = r.ModifySize ?? ReadUInt16(r.RecordBytes, LogRecordPayload.ModifySizeOffset) ?? elements[0].Length;
                op.Patch = RowPatch.FromModifyRow(offset, size, elements[0], elements[1], r.IsCompensation);
            }
        }
        else if (RowPatch.TryFromModifyColumns(elements, out var patch))
        {
            op.Patch = patch;
        }
        else
        {
            op.Problem = "the LOP_MODIFY_COLUMNS record could not be parsed";
        }

        if (table.IsHeap)
        {
            op.Identity = RowIdentity.ForHeap(r);
        }
        else if (elements.Count > 2 && elements[2].Length > 0 && RowDecoder.TryDecodeKey(table, elements[2], out var key))
        {
            op.Key = key;
            op.Identity = RowIdentity.ForClusteredKey(table, key);
        }
        return op;
    }

    private static int? ReadUInt16(byte[]? bytes, int offset) =>
        bytes != null && bytes.Length >= offset + 2 ? BitConverter.ToUInt16(bytes, offset) : null;

    /// <summary>
    /// Compensation records undo a transaction's operations in reverse order. Pair every compensation
    /// record with the operation it undoes so that both share the row identity.
    /// </summary>
    private static void PairCompensations(List<RowOp> ops, List<string> warnings)
    {
        var stacks = new Dictionary<TransactionInfo, Stack<RowOp>>();
        int unpaired = 0;
        foreach (var op in ops)
        {
            if (!stacks.TryGetValue(op.Transaction, out var stack))
                stacks[op.Transaction] = stack = new Stack<RowOp>();

            if (!op.IsCompensation)
            {
                stack.Push(op);
                continue;
            }

            if (stack.Count == 0 || stack.Peek().Table.AllocationUnitId != op.Table.AllocationUnitId || !IsInverse(stack.Peek().Kind, op.Kind))
            {
                unpaired++;
                continue;
            }

            var original = stack.Pop();
            original.CompensatedBy = op.Index;
            op.Compensates = original;
            op.Identity ??= original.Identity;
            original.Identity ??= op.Identity;
        }
        if (unpaired > 0)
            warnings.Add($"{unpaired} compensation (rollback) record(s) could not be matched to the operation they undo.");
    }

    private static bool IsInverse(ChangeOperation original, ChangeOperation compensation) => (original, compensation) switch
    {
        (ChangeOperation.Insert, ChangeOperation.Delete) => true,
        (ChangeOperation.Delete, ChangeOperation.Insert) => true,
        (ChangeOperation.Update, ChangeOperation.Update) => true,
        _ => false,
    };

    // ---------------------------------------------------------------- current data

    private async Task AnchorOnCurrentDataAsync(List<RowHistory> histories, AnalyzerOptions options, List<string> warnings,
        IProgress<string>? progress, CancellationToken ct)
    {
        var pending = histories.Where(h => h.NeedsCurrentImage).ToList();
        if (pending.Count == 0) return;
        if (pending.Count > options.MaxCurrentRowLookups)
        {
            warnings.Add($"{pending.Count} updated rows need their current image; only the first {options.MaxCurrentRowLookups} were looked up.");
            pending = pending.Take(options.MaxCurrentRowLookups).ToList();
        }

        int done = 0;
        foreach (var history in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 200 == 0) progress?.Report($"Reading current row images ({done}/{pending.Count})...");
            var locator = history.CreateLocator();
            if (locator == null) continue;
            byte[]? current;
            try
            {
                current = await _currentRows!.GetCurrentRowAsync(locator, ct);
            }
            catch (CurrentRowSourceUnavailableException ex)
            {
                warnings.Add(ex.Message);
                return;
            }
            if (current != null && history.TrySetCurrentImage(current))
                history.Propagate();
        }
    }

    // ---------------------------------------------------------------- output

    private static RowChange? BuildChange(RowOp op)
    {
        var table = op.Table;
        var notes = new List<string>();
        if (op.Problem != null) notes.Add(op.Problem);
        if (op.CompensatedBy.HasValue && op.Transaction.State != TransactionState.RolledBack)
            notes.Add("undone by a rollback to a savepoint");

        DecodedRow? before = DecodeImage(table, op.BeforeImage, notes, "before");
        DecodedRow? after = DecodeImage(table, op.AfterImage, notes, "after");
        var columns = new List<ColumnChange>();
        bool complete;

        switch (op.Kind)
        {
            case ChangeOperation.Insert:
                if (after == null) return null;
                columns.AddRange(after.Values.Select(v => new ColumnChange { Column = v.Column, After = v, IsChanged = true }));
                complete = after.Values.All(v => v.State != ValueState.Error);
                break;

            case ChangeOperation.Delete:
                if (before == null) return null;
                columns.AddRange(before.Values.Select(v => new ColumnChange { Column = v.Column, Before = v, IsChanged = true }));
                complete = before.Values.All(v => v.State != ValueState.Error);
                break;

            default:
                if (op.Patch == null) return null;
                if (before != null && after != null)
                {
                    for (int i = 0; i < before.Values.Count; i++)
                    {
                        var b = before.Values[i];
                        var a = after.Values[i];
                        columns.Add(new ColumnChange { Column = b.Column, Before = b, After = a, IsChanged = !b.SameAs(a) });
                    }
                    complete = true;
                }
                else
                {
                    columns.AddRange(PartialUpdateDecoder.Decode(table, op.Patch));
                    complete = false;
                    notes.Add("The full row image is not available (row inserted before the log that was read); " +
                              "only fixed-length columns could be decoded.");
                    if (op.IsCompensation) notes.Add("compensation record");
                }
                break;
        }

        return new RowChange
        {
            Lsn = op.Record.Lsn,
            Operation = op.Kind,
            Table = table,
            Transaction = op.Transaction,
            Record = op.Record,
            Before = before,
            After = after,
            Key = op.Key,
            Columns = columns,
            IsComplete = complete,
            Notes = notes,
        };
    }

    private static DecodedRow? DecodeImage(TableInfo table, byte[]? image, List<string> notes, string which)
    {
        if (image == null) return null;
        if (RowDecoder.TryDecode(table, image, out var row, out var error)) return row;
        notes.Add($"The {which} image could not be decoded: {error}");
        return null;
    }

    private static bool Matches(RowOp op, AnalyzerOptions options)
    {
        bool rolledBack = op.Transaction.State == TransactionState.RolledBack || op.CompensatedBy.HasValue;
        if (rolledBack && !options.IncludeRolledBack) return false;
        if (options.Operations.Count > 0 && !options.Operations.Contains(op.Kind)) return false;
        if (options.Tables.Count > 0 && !options.Tables.Any(op.Table.MatchesName)) return false;
        return TransactionMatches(op.Transaction, options);
    }

    private static bool MatchesTableName(string displayName, string filter)
    {
        var f = filter.Replace("[", "").Replace("]", "").Trim();
        if (f.Contains('.')) return string.Equals(displayName, f, StringComparison.OrdinalIgnoreCase);
        int dot = displayName.IndexOf('.');
        return string.Equals(dot >= 0 ? displayName[(dot + 1)..] : displayName, f, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] DdlPrefixes = { "CREATE", "ALTER", "DROP", "TRUNCATE", "SELECT INTO", "sp_rename", "RENAME" };

    /// <summary>Schema changes are reported in the transaction list even though they carry no row changes.</summary>
    public static bool IsDdlTransaction(TransactionInfo tx) =>
        tx.Name != null && DdlPrefixes.Any(p => tx.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool TransactionMatches(TransactionInfo tx, AnalyzerOptions options)
    {
        if (options.TransactionId != null && !string.Equals(tx.TransactionId, options.TransactionId.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(options.UserName) &&
            (tx.UserName == null || tx.UserName.IndexOf(options.UserName.Trim(), StringComparison.OrdinalIgnoreCase) < 0))
            return false;
        if (options.From.HasValue || options.To.HasValue)
        {
            var time = tx.BeginTime ?? tx.EndTime;
            if (time == null) return false;
            if (options.From.HasValue && time < options.From) return false;
            if (options.To.HasValue && time > options.To) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- helpers

    internal sealed class RowOp
    {
        public required int Index { get; init; }
        public required LogRecord Record { get; init; }
        public required TableInfo Table { get; init; }
        public required TransactionInfo Transaction { get; init; }
        public required ChangeOperation Kind { get; init; }
        public bool IsCompensation { get; init; }
        public string? Identity { get; set; }
        public byte[]? Row { get; set; }
        public RowPatch? Patch { get; set; }
        public DecodedRow? Key { get; set; }
        /// <summary>Internal row movement (heap forwarding), not a user change.</summary>
        public bool IsInternal { get; set; }
        public RecordType? RecordType { get; set; }
        public string? Problem { get; set; }
        public int? CompensatedBy { get; set; }
        public RowOp? Compensates { get; set; }
        public byte[]? BeforeImage { get; set; }
        public byte[]? AfterImage { get; set; }

        public bool Decodable => Kind == ChangeOperation.Update ? Patch != null : Row != null && Problem == null;
    }

    /// <summary>
    /// The ordered operations on one physical row. <c>images[k]</c> is the row before operation k,
    /// <c>images[n]</c> the row after the last one. Known images are propagated through the
    /// operations in both directions until nothing changes.
    /// </summary>
    private sealed class RowHistory
    {
        private readonly List<RowOp> _ops;
        private readonly byte[]?[] _images;
        private readonly bool[] _absent;
        private readonly Dictionary<int, int> _positionByIndex;

        public RowHistory(List<RowOp> ops)
        {
            _ops = ops;
            _images = new byte[]?[ops.Count + 1];
            _absent = new bool[ops.Count + 1];
            _positionByIndex = new Dictionary<int, int>();
            for (int k = 0; k < ops.Count; k++)
            {
                _positionByIndex[ops[k].Index] = k;
                var op = ops[k];
                if (op.Row == null) continue;
                if (op.Kind == ChangeOperation.Insert)
                {
                    _images[k + 1] = op.Row;
                    _absent[k] = true;
                }
                else if (op.Kind == ChangeOperation.Delete)
                {
                    _images[k] = op.Row;
                    _absent[k + 1] = true;
                }
            }
        }

        public bool NeedsCurrentImage =>
            !_absent[_ops.Count] && _images[_ops.Count] == null
            && _ops.Any(o => o.Kind == ChangeOperation.Update && o.Patch != null)
            && Enumerable.Range(0, _ops.Count).Any(k => _ops[k].Kind == ChangeOperation.Update && (_images[k] == null || _images[k + 1] == null));

        public RowLocator? CreateLocator()
        {
            var last = _ops[^1];
            var table = last.Table;
            if (table.IsHeap)
            {
                if (!last.Record.TryGetPage(out var file, out var page) || last.Record.SlotId is not int slot) return null;
                return new RowLocator { Table = table, Identity = last.Identity!, FileId = file, PageId = page, SlotId = slot };
            }

            var key = _ops.Select(o => o.Key).LastOrDefault(k => k != null);
            if (key == null)
            {
                foreach (var image in _images)
                {
                    if (image != null && RowDecoder.TryDecode(table, image, out var decoded, out _))
                    {
                        key = decoded;
                        break;
                    }
                }
            }
            return key == null ? null : new RowLocator { Table = table, Identity = last.Identity!, Key = key };
        }

        public bool TrySetCurrentImage(byte[] current)
        {
            int n = _ops.Count;
            if (_images[n] != null || _absent[n]) return false;
            // Only accept the image if it is consistent with the last operation.
            var last = _ops[n - 1];
            if (last.Kind == ChangeOperation.Update && last.Patch != null && !last.Patch.TryRevert(current, out _))
                return false;
            _images[n] = current;
            return true;
        }

        public void Propagate()
        {
            bool changed = true;
            int guard = 0;
            while (changed && guard++ < 50)
            {
                changed = false;
                for (int k = 0; k < _ops.Count; k++)
                {
                    var patch = _ops[k].Patch;
                    if (patch == null) continue;
                    if (_images[k] != null && _images[k + 1] == null && !_absent[k + 1] && patch.TryApply(_images[k]!, out var after))
                    {
                        _images[k + 1] = after;
                        changed = true;
                    }
                }
                for (int k = _ops.Count - 1; k >= 0; k--)
                {
                    var patch = _ops[k].Patch;
                    if (patch == null) continue;
                    if (_images[k + 1] != null && _images[k] == null && !_absent[k] && patch.TryRevert(_images[k + 1]!, out var before))
                    {
                        _images[k] = before;
                        changed = true;
                    }
                }
                // A compensation record restores the row to what it was before the operation it undoes.
                for (int k = 0; k < _ops.Count; k++)
                {
                    var original = _ops[k].Compensates;
                    if (original == null || !_positionByIndex.TryGetValue(original.Index, out int j)) continue;
                    changed |= Link(j, k + 1);
                    changed |= Link(j + 1, k);
                }
            }
        }

        private bool Link(int a, int b)
        {
            if (_images[a] != null && _images[b] == null && !_absent[b]) { _images[b] = _images[a]; return true; }
            if (_images[b] != null && _images[a] == null && !_absent[a]) { _images[a] = _images[b]; return true; }
            if (_absent[a] && !_absent[b] && _images[b] == null) { _absent[b] = true; return true; }
            if (_absent[b] && !_absent[a] && _images[a] == null) { _absent[a] = true; return true; }
            return false;
        }

        public void Publish()
        {
            for (int k = 0; k < _ops.Count; k++)
            {
                var op = _ops[k];
                switch (op.Kind)
                {
                    case ChangeOperation.Insert:
                        op.AfterImage = op.Row ?? _images[k + 1];
                        break;
                    case ChangeOperation.Delete:
                        op.BeforeImage = op.Row ?? _images[k];
                        break;
                    default:
                        op.BeforeImage = _images[k];
                        op.AfterImage = _images[k + 1];
                        break;
                }
            }
        }
    }
}

/// <summary>Thrown by an <see cref="ICurrentRowSource"/> that cannot be used at all (e.g. missing permission).</summary>
public sealed class CurrentRowSourceUnavailableException : Exception
{
    public CurrentRowSourceUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
