using System.Globalization;
using SqlLogReader.Analysis;

namespace SqlLogReader.WinForms;

/// <summary>Grid row for a decoded change.</summary>
internal sealed class ChangeView
{
    public ChangeView(RowChange change)
    {
        Change = change;
        Lsn = change.Lsn.ToString();
        Time = change.Time?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture) ?? "";
        User = change.UserName;
        Operation = change.Operation.ToString().ToUpperInvariant() + (change.IsComplete ? "" : " *");
        Table = change.TableName;
        Key = change.KeyText;
        Summary = change.Summary;
        TransactionId = change.Transaction.TransactionId;
        TransactionName = change.Transaction.Name ?? "";
        State = change.Transaction.State.ToString();
        _searchText = string.Join("\u0001", Lsn, Time, User, Operation, Table, Key, Summary, TransactionId, TransactionName, State);
    }

    private readonly string _searchText;

    public RowChange Change { get; }
    public string Lsn { get; }
    public string Time { get; }
    public string User { get; }
    public string Operation { get; }
    public string Table { get; }
    public string Key { get; }
    public string Summary { get; }
    public string TransactionId { get; }
    public string TransactionName { get; }
    public string State { get; }

    public bool Matches(string text) => _searchText.Contains(text, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>Grid row for a transaction.</summary>
internal sealed class TransactionView
{
    public TransactionView(TransactionInfo tx)
    {
        Transaction = tx;
        TransactionId = tx.TransactionId;
        Name = tx.Name ?? "";
        Begin = tx.BeginTime?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture) ?? "";
        End = tx.EndTime?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.CurrentCulture) ?? "";
        User = tx.DisplayUser;
        State = tx.State.ToString();
        Rows = tx.ChangeCount;
        Tables = string.Join(", ", tx.Tables);
    }

    public TransactionInfo Transaction { get; }
    public string TransactionId { get; }
    public string Name { get; }
    public string Begin { get; }
    public string End { get; }
    public string User { get; }
    public string State { get; }
    public int Rows { get; }
    public string Tables { get; }
}
