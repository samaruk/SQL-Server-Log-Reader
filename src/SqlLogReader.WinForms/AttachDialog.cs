namespace SqlLogReader.WinForms;

/// <summary>Asks for the .mdf / .ldf files to attach and the name of the new database.</summary>
internal sealed class AttachDialog : Form
{
    private readonly TextBox _name = new() { Width = 420 };
    private readonly TextBox _mdf = new() { Width = 420 };
    private readonly TextBox _ldf = new() { Width = 420 };

    public AttachDialog()
    {
        Text = "Attach database files";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Padding = new Padding(10), Dock = DockStyle.Fill };
        layout.Controls.Add(new Label
        {
            Text = "The files are opened by the SQL Server service, so use paths as seen from the server and make sure the service " +
                   "account can read and write them. Attaching runs recovery, which writes to the log: attach copies of the files.",
            MaximumSize = new Size(620, 0),
            AutoSize = true,
            Margin = new Padding(3, 3, 3, 10),
        }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 3);

        AddRow(layout, 1, "Data file (.mdf):", _mdf, "Data files (*.mdf)|*.mdf|All files|*.*");
        AddRow(layout, 2, "Log file (.ldf):", _ldf, "Log files (*.ldf)|*.ldf|All files|*.*");
        layout.Controls.Add(new Label { Text = "New database name:", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, 3);
        layout.Controls.Add(_name, 1, 3);

        var ok = new Button { Text = "Attach", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 0, 4);
        layout.SetColumnSpan(buttons, 3);

        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_mdf.Text) || string.IsNullOrWhiteSpace(_ldf.Text) || string.IsNullOrWhiteSpace(_name.Text))
            {
                MessageBox.Show(this, "Enter the data file, the log file and a database name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        _mdf.TextChanged += (_, _) =>
        {
            if (_name.Modified) return;
            _name.Text = Path.GetFileNameWithoutExtension(_mdf.Text) + "_LogReader";
        };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    public string DatabaseName => _name.Text.Trim();
    public string DataFile => _mdf.Text.Trim();
    public string LogFile => _ldf.Text.Trim();

    private void AddRow(TableLayoutPanel layout, int row, string label, TextBox box, string filter)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, row);
        layout.Controls.Add(box, 1, row);
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = filter };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            box.Text = dialog.FileName;
            if (box == _mdf && string.IsNullOrWhiteSpace(_ldf.Text))
            {
                var guess = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName) + "_log.ldf");
                if (File.Exists(guess)) _ldf.Text = guess;
            }
        };
        layout.Controls.Add(browse, 2, row);
    }
}
