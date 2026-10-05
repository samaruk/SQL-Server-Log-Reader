using System.Globalization;

namespace SqlLogReader.WinForms;

/// <summary>Shows rows in a grid; optionally lets the user pick some of them.</summary>
internal sealed class ListDialog : Form
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        BackgroundColor = SystemColors.Window,
    };

    public ListDialog(string title, IReadOnlyList<string> headers, IReadOnlyList<object[]> rows, bool selectable)
    {
        Text = title;
        Width = 1000;
        Height = 500;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        MinimizeBox = false;
        ShowInTaskbar = false;

        foreach (var h in headers) _grid.Columns.Add(h, h);
        foreach (var row in rows)
            _grid.Rows.Add(row.Select(v => v switch
            {
                DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture),
                bool b => b ? "yes" : "no",
                _ => Convert.ToString(v, CultureInfo.CurrentCulture),
            }).ToArray<object?>());

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        var close = new Button { Text = selectable ? "Cancel" : "Close", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(close);
        CancelButton = close;
        if (selectable)
        {
            var ok = new Button { Text = "Use selected", DialogResult = DialogResult.OK, AutoSize = true };
            buttons.Controls.Add(ok);
            AcceptButton = ok;
            _grid.CellDoubleClick += (_, e) =>
            {
                if (e.RowIndex < 0) return;
                DialogResult = DialogResult.OK;
                Close();
            };
        }

        Controls.Add(_grid);
        Controls.Add(buttons);
    }

    /// <summary>Indexes (into the rows passed in) of the selected rows.</summary>
    public IReadOnlyList<int> SelectedIndexes =>
        _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).OrderBy(i => i).ToList();
}
