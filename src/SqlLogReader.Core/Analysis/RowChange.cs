using SqlLogReader.Decoding;
using SqlLogReader.Log;
using SqlLogReader.Schema;

namespace SqlLogReader.Analysis;

public enum ChangeOperation
{
    Insert,
    Update,
    Delete,
}

/// <summary>Old and new value of one column of a changed row.</summary>
public sealed class ColumnChange
{
    public required ColumnInfo Column { get; init; }
    public ColumnValue? Before { get; init; }
    public ColumnValue? After { get; init; }
    public bool IsChanged { get; init; }

    public string Name => Column.Name;
}

/// <summary>A decoded INSERT, UPDATE or DELETE of one row.</summary>
public sealed class RowChange
{
    public required Lsn Lsn { get; init; }
    public required ChangeOperation Operation { get; init; }
    public required TableInfo Table { get; init; }
    public required TransactionInfo Transaction { get; init; }
    public required LogRecord Record { get; init; }

    /// <summary>Row before the change (deletes, updates).</summary>
    public DecodedRow? Before { get; init; }

    /// <summary>Row after the change (inserts, updates).</summary>
    public DecodedRow? After { get; init; }

    /// <summary>Clustered key values carried by update records.</summary>
    public DecodedRow? Key { get; init; }

    /// <summary>Per-column values for every column of the table.</summary>
    public IReadOnlyList<ColumnChange> Columns { get; init; } = Array.Empty<ColumnChange>();

    /// <summary>False when only part of the row could be reconstructed (see <see cref="Notes"/>).</summary>
    public bool IsComplete { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public string TableName => Table.DisplayName;
    public DateTime? Time => Transaction.EndTime ?? Transaction.BeginTime;
    public string UserName => Transaction.DisplayUser;

    public IEnumerable<ColumnChange> ChangedColumns => Columns.Where(c => c.IsChanged);

    /// <summary>Values that identify the row, e.g. <c>PatientId = 2</c>.</summary>
    public string KeyText
    {
        get
        {
            var id = RowIdentification.For(this, preferBefore: Operation != ChangeOperation.Insert);
            if (id == null || id.Values.Count == 0) return "";
            var shown = id.Values.Take(4).Select(v => $"{v.Name} = {Truncate(v.DisplayText, 40)}");
            return string.Join(", ", shown) + (id.Values.Count > 4 ? ", ..." : "");
        }
    }

    /// <summary>One-line description of the change.</summary>
    public string Summary
    {
        get
        {
            switch (Operation)
            {
                case ChangeOperation.Update:
                    var changed = ChangedColumns.Select(c =>
                        $"{c.Name}: {Truncate(c.Before?.DisplayText ?? "?", 40)} -> {Truncate(c.After?.DisplayText ?? "?", 40)}").ToArray();
                    return changed.Length == 0 ? "(no column change decoded)" : string.Join("; ", changed);
                default:
                    var values = Columns.Select(c => (c.After ?? c.Before) is { } v ? $"{c.Name} = {Truncate(v.DisplayText, 40)}" : null)
                        .Where(s => s != null).ToArray();
                    return string.Join("; ", values);
            }
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    public override string ToString() => $"{Lsn} {Operation} {TableName} {KeyText}";
}
