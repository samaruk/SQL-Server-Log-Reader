using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlLogReader.Analysis;
using SqlLogReader.Export;
using SqlLogReader.Log;
using SqlLogReader.Scripting;
using SqlLogReader.Server;

namespace SqlLogReader.WinForms;

/// <summary>Main window: connect, pick a database and log source, read and browse the decoded changes.</summary>
internal sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();

    // connection
    private readonly TextBox _server = new() { Width = 200 };
    private readonly ComboBox _auth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly TextBox _login = new() { Width = 120 };
    private readonly TextBox _password = new() { Width = 120, UseSystemPasswordChar = true };
    private readonly CheckBox _trustCertificate = new() { Text = "Trust server certificate", AutoSize = true, Checked = true };
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true };

    // source
    private readonly ComboBox _database = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly CheckBox _onlineLog = new() { Text = "Online log", AutoSize = true, Checked = true };
    private readonly CheckBox _inactiveLog = new() { Text = "Include inactive log (TF 2537)", AutoSize = true };
    private readonly TextBox _backups = new() { Width = 320, PlaceholderText = "Log backup files on the server (separate with ;)" };
    private readonly Button _browseBackups = new() { Text = "Browse...", AutoSize = true };
    private readonly Button _backupHistory = new() { Text = "From backup history...", AutoSize = true };

    // filters
    private readonly CheckBox _useFrom = new() { Text = "From", AutoSize = true };
    private readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 150 };
    private readonly CheckBox _useTo = new() { Text = "To", AutoSize = true };
    private readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy-MM-dd HH:mm:ss", Width = 150 };
    private readonly ComboBox _table = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly CheckBox _insert = new() { Text = "Insert", AutoSize = true, Checked = true };
    private readonly CheckBox _update = new() { Text = "Update", AutoSize = true, Checked = true };
    private readonly CheckBox _delete = new() { Text = "Delete", AutoSize = true, Checked = true };
    private readonly TextBox _userFilter = new() { Width = 110, PlaceholderText = "any login" };
    private readonly CheckBox _rolledBack = new() { Text = "Include rolled back", AutoSize = true };
    private readonly CheckBox _currentData = new() { Text = "Use current data for old values", AutoSize = true, Checked = true };
    private readonly Button _read = new() { Text = "Read log", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };

    // results
    private readonly TabControl _mainTabs = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly Font _boldFont;
    private readonly TextBox _search = new() { Dock = DockStyle.Top, PlaceholderText = "Search changes (table, key, value, user, transaction...)" };
    private readonly DataGridView _changesGrid = CreateGrid();
    private readonly DataGridView _valuesGrid = CreateGrid();
    private readonly DataGridView _transactionsGrid = CreateGrid();
    private readonly TextBox _undoText = CreateCodeBox();
    private readonly TextBox _redoText = CreateCodeBox();
    private readonly TextBox _recordText = CreateCodeBox();
    private readonly TextBox _messages = CreateCodeBox();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Not connected." };
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 160 };

    private CancellationTokenSource? _cts;
    private LogReadResult? _result;
    private List<ChangeView> _allChanges = new();
    private List<ChangeView> _visibleChanges = new();

    public MainForm()
    {
        Text = "SQL Server Log Reader";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        _boldFont = new Font(Font, FontStyle.Bold);
        _auth.Items.AddRange(new object[] { "Windows Authentication", "SQL Server Authentication" });

        BuildLayout();
        WireEvents();
        LoadSettings();
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var connectionRow = Row(
            Label("Server:"), _server, Label("Authentication:"), _auth, Label("Login:"), _login, Label("Password:"), _password,
            _trustCertificate, _connect);
        var sourceRow = Row(
            Label("Database:"), _database, _onlineLog, _inactiveLog, Label("Log backups:"), _backups, _browseBackups, _backupHistory);
        var filterRow = Row(
            _useFrom, _from, _useTo, _to, Label("Table:"), _table, _insert, _update, _delete, Label("Login:"), _userFilter);
        var actionRow = Row(_rolledBack, _currentData, _read, _cancel);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(6, 4, 6, 0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        header.Controls.Add(Group("Connection", connectionRow));
        header.Controls.Add(Group("Log source", sourceRow));
        header.Controls.Add(Group("Filters", filterRow));
        header.Controls.Add(actionRow);

        // Row changes tab: list on top, details below.
        var details = new TabControl { Dock = DockStyle.Fill };
        details.TabPages.Add(Page("Row values", _valuesGrid));
        details.TabPages.Add(Page("Undo script", _undoText));
        details.TabPages.Add(Page("Redo script", _redoText));
        details.TabPages.Add(Page("Log record", _recordText));

        _split.Panel1.Controls.Add(_changesGrid);
        _split.Panel1.Controls.Add(_search);
        _split.Panel2.Controls.Add(details);

        _mainTabs.TabPages.Add(Page("Row changes", _split));
        _mainTabs.TabPages.Add(Page("Transactions", _transactionsGrid));
        _mainTabs.TabPages.Add(Page("Messages", _messages));

        ConfigureChangesGrid();
        ConfigureValuesGrid();
        ConfigureTransactionsGrid();

        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_progress);

        // Docking is applied from the last added control to the first.
        Controls.Add(_mainTabs);
        Controls.Add(header);
        Controls.Add(BuildMenu());
        Controls.Add(statusStrip);

        // The splitter can only be positioned once the container has its real size.
        Load += (_, _) => _split.SplitterDistance = Math.Max(_split.Panel1MinSize, _split.Height * 55 / 100);
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Open .ldf log file...", null, async (_, _) => await ReadLdfFileAsync());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Export changes to &CSV...", null, (_, _) => ExportChanges("CSV file|*.csv", ".csv"));
        file.DropDownItems.Add("Export changes to &JSON...", null, (_, _) => ExportChanges("JSON file|*.json", ".json"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Save &undo script...", null, (_, _) => SaveScript(ScriptKind.Undo, selectedOnly: false));
        file.DropDownItems.Add("Save &redo script...", null, (_, _) => SaveScript(ScriptKind.Redo, selectedOnly: false));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Attach database files (.mdf / .ldf)...", null, async (_, _) => await AttachDatabaseAsync());
        tools.DropDownItems.Add("Log &backup history...", null, async (_, _) => await PickBackupsAsync());
        tools.DropDownItems.Add("&Virtual log files...", null, async (_, _) => await ShowVirtualLogFilesAsync());

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&About", null, (_, _) => MessageBox.Show(this,
            "SQL Server Log Reader\n\nDecodes INSERT, UPDATE and DELETE operations from the SQL Server transaction log " +
            "(online log via fn_dblog, log backups via fn_dump_dblog) and generates undo / redo scripts.\n\n" +
            "Reading the log requires membership in the sysadmin server role.",
            "About", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.Add(file);
        menu.Items.Add(tools);
        menu.Items.Add(help);
        MainMenuStrip = menu;
        return menu;
    }

    private void ConfigureChangesGrid()
    {
        AddColumn(_changesGrid, nameof(ChangeView.Lsn), "LSN", 150);
        AddColumn(_changesGrid, nameof(ChangeView.Time), "Time", 150);
        AddColumn(_changesGrid, nameof(ChangeView.User), "User", 110);
        AddColumn(_changesGrid, nameof(ChangeView.Operation), "Operation", 80);
        AddColumn(_changesGrid, nameof(ChangeView.Table), "Table", 160);
        AddColumn(_changesGrid, nameof(ChangeView.Key), "Key", 160);
        AddColumn(_changesGrid, nameof(ChangeView.Summary), "Changes", 450);
        AddColumn(_changesGrid, nameof(ChangeView.TransactionName), "Transaction", 110);
        AddColumn(_changesGrid, nameof(ChangeView.TransactionId), "Transaction ID", 110);
        AddColumn(_changesGrid, nameof(ChangeView.State), "State", 80);
        _changesGrid.MultiSelect = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy undo script for selected rows", null, (_, _) => CopyScript(ScriptKind.Undo));
        menu.Items.Add("Copy redo script for selected rows", null, (_, _) => CopyScript(ScriptKind.Redo));
        menu.Items.Add("Save undo script for selected rows...", null, (_, _) => SaveScript(ScriptKind.Undo, selectedOnly: true));
        menu.Items.Add("Save redo script for selected rows...", null, (_, _) => SaveScript(ScriptKind.Redo, selectedOnly: true));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show only this transaction", null, (_, _) =>
        {
            if (SelectedChanges().FirstOrDefault() is { } c) _search.Text = c.Transaction.TransactionId;
        });
        _changesGrid.ContextMenuStrip = menu;
    }

    private void ConfigureValuesGrid()
    {
        _valuesGrid.Columns.Add("Column", "Column");
        _valuesGrid.Columns.Add("Type", "Type");
        _valuesGrid.Columns.Add("Old", "Old value");
        _valuesGrid.Columns.Add("New", "New value");
        _valuesGrid.Columns[0].Width = 180;
        _valuesGrid.Columns[1].Width = 130;
        _valuesGrid.Columns[2].Width = 380;
        _valuesGrid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
    }

    private void ConfigureTransactionsGrid()
    {
        AddColumn(_transactionsGrid, nameof(TransactionView.TransactionId), "Transaction ID", 110);
        AddColumn(_transactionsGrid, nameof(TransactionView.Name), "Name", 160);
        AddColumn(_transactionsGrid, nameof(TransactionView.Begin), "Begin", 160);
        AddColumn(_transactionsGrid, nameof(TransactionView.End), "End", 160);
        AddColumn(_transactionsGrid, nameof(TransactionView.User), "User", 140);
        AddColumn(_transactionsGrid, nameof(TransactionView.State), "State", 90);
        AddColumn(_transactionsGrid, nameof(TransactionView.Rows), "Rows", 60);
        AddColumn(_transactionsGrid, nameof(TransactionView.Tables), "Tables", 400);
    }

    // ------------------------------------------------------------------ events

    private void WireEvents()
    {
        _auth.SelectedIndexChanged += (_, _) =>
        {
            bool sql = _auth.SelectedIndex == 1;
            _login.Enabled = sql;
            _password.Enabled = sql;
        };
        _connect.Click += async (_, _) => await ConnectAsync();
        _read.Click += async (_, _) => await ReadLogAsync();
        _cancel.Click += (_, _) => _cts?.Cancel();
        _browseBackups.Click += (_, _) => BrowseBackups();
        _backupHistory.Click += async (_, _) => await PickBackupsAsync();
        _database.SelectedIndexChanged += async (_, _) => await LoadTablesAsync();
        _search.TextChanged += (_, _) => ApplySearch();
        _changesGrid.SelectionChanged += (_, _) => ShowSelectedChange();
        _transactionsGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || _transactionsGrid.Rows[e.RowIndex].DataBoundItem is not TransactionView tv) return;
            _search.Text = tv.TransactionId;
            _mainTabs.SelectedIndex = 0;
        };
        _valuesGrid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _valuesGrid.Rows[e.RowIndex].Tag is true && e.CellStyle != null)
            {
                e.CellStyle.BackColor = Color.LightYellow;
                e.CellStyle.Font = _boldFont;
            }
        };
        FormClosing += (_, _) =>
        {
            _cts?.Cancel();
            SaveSettings();
        };
    }

    private void LoadSettings()
    {
        _server.Text = _settings.Server;
        _auth.SelectedIndex = _settings.WindowsAuthentication ? 0 : 1;
        _login.Text = _settings.Login;
        _trustCertificate.Checked = _settings.TrustServerCertificate;
        _from.Value = DateTime.Today;
        _to.Value = DateTime.Today.AddDays(1).AddSeconds(-1);
    }

    private void SaveSettings()
    {
        _settings.Server = _server.Text.Trim();
        _settings.WindowsAuthentication = _auth.SelectedIndex == 0;
        _settings.Login = _login.Text.Trim();
        _settings.TrustServerCertificate = _trustCertificate.Checked;
        if (_database.SelectedItem is DatabaseItem db) _settings.Database = db.Summary.Name;
        _settings.Save();
    }

    private ConnectionSettings CurrentConnection() => new()
    {
        Server = _server.Text.Trim(),
        IntegratedSecurity = _auth.SelectedIndex == 0,
        UserName = _login.Text.Trim(),
        Password = _password.Text,
        TrustServerCertificate = _trustCertificate.Checked,
    };

    private string? SelectedDatabase => (_database.SelectedItem as DatabaseItem)?.Summary.Name;

    private async Task ConnectAsync()
    {
        await RunBusyAsync("Connecting...", async ct =>
        {
            await using var connection = await CurrentConnection().OpenAsync("master", ct);
            var server = await ServerExplorer.GetServerSummaryAsync(connection, ct);
            var databases = await ServerExplorer.ListDatabasesAsync(connection, ct);

            _database.Items.Clear();
            foreach (var d in databases) _database.Items.Add(new DatabaseItem(d));
            var preferred = databases.FindIndex(d => string.Equals(d.Name, _settings.Database, StringComparison.OrdinalIgnoreCase));
            if (_database.Items.Count > 0) _database.SelectedIndex = Math.Max(0, preferred);

            _read.Enabled = _database.Items.Count > 0;
            var status = $"Connected to {server.ServerName} (SQL Server {server.Version}, {server.Edition}).";
            if (!server.IsSysAdmin)
                status += " WARNING: the login is not sysadmin; reading the log will fail.";
            SetStatus(status);
            SaveSettings();
        });
    }

    private async Task LoadTablesAsync()
    {
        var db = SelectedDatabase;
        if (db == null || _cts != null) return;
        try
        {
            await using var connection = await CurrentConnection().OpenAsync(db);
            await using var cmd = new SqlCommand(
                "SELECT s.name + '.' + t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id ORDER BY 1", connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            var names = new List<string>();
            while (await reader.ReadAsync()) names.Add(reader.GetString(0));
            var text = _table.Text;
            _table.Items.Clear();
            _table.Items.Add("");
            foreach (var n in names) _table.Items.Add(n);
            _table.Text = text;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            Log("Could not list tables: " + ex.Message);
        }
    }

    private AnalyzerOptions? BuildAnalyzerOptions()
    {
        var operations = new List<ChangeOperation>();
        if (_insert.Checked) operations.Add(ChangeOperation.Insert);
        if (_update.Checked) operations.Add(ChangeOperation.Update);
        if (_delete.Checked) operations.Add(ChangeOperation.Delete);
        if (operations.Count == 0)
        {
            MessageBox.Show(this, "Select at least one operation.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        return new AnalyzerOptions
        {
            From = _useFrom.Checked ? _from.Value : null,
            To = _useTo.Checked ? _to.Value : null,
            Tables = string.IsNullOrWhiteSpace(_table.Text) ? Array.Empty<string>() : new[] { _table.Text.Trim() },
            Operations = operations.Count == 3 ? Array.Empty<ChangeOperation>() : operations,
            UserName = string.IsNullOrWhiteSpace(_userFilter.Text) ? null : _userFilter.Text.Trim(),
            IncludeRolledBack = _rolledBack.Checked,
            UseCurrentData = _currentData.Checked,
        };
    }

    private async Task ReadLogAsync()
    {
        var db = SelectedDatabase;
        if (db == null) return;
        var analysis = BuildAnalyzerOptions();
        if (analysis == null) return;

        var backups = _backups.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!_onlineLog.Checked && backups.Length == 0)
        {
            MessageBox.Show(this, "Select the online log and/or add log backup files.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var request = new LogReadRequest
        {
            Connection = CurrentConnection(),
            Database = db,
            Source = new LogSourceOptions
            {
                IncludeOnlineLog = _onlineLog.Checked,
                IncludeInactiveLog = _inactiveLog.Checked,
                BackupFiles = backups,
            },
            Analysis = analysis,
        };

        await RunBusyAsync("Reading the transaction log...", async ct =>
        {
            var progress = new Progress<string>(SetStatus);
            var result = await Task.Run(() => LogReaderService.ReadAsync(request, progress, ct), ct);
            ShowResult(result);
        });
    }

    /// <summary>Reads an .ldf file directly; the selected database (if any) supplies the table layouts.</summary>
    private async Task ReadLdfFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "SQL Server log files (*.ldf)|*.ldf|All files|*.*",
            Title = "Open the transaction log file(s) of a database (a detached database or a copy)",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var analysis = BuildAnalyzerOptions();
        if (analysis == null) return;

        var db = SelectedDatabase;
        if (db == null)
        {
            var answer = MessageBox.Show(this,
                "To decode row values, connect and select a database that has the same tables as the log " +
                "(for example the original database or an attached copy of its .mdf).\n\n" +
                "Continue without one? Only the transactions will be listed.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            analysis.IncludeAllTransactions = true;
        }

        var paths = dialog.FileNames;
        var connection = db == null ? null : CurrentConnection();
        await RunBusyAsync($"Reading {string.Join(", ", paths.Select(Path.GetFileName))}...", async ct =>
        {
            var progress = new Progress<string>(SetStatus);
            var result = await Task.Run(() => LogReaderService.ReadLdfFilesAsync(paths, connection, db, analysis, progress, ct), ct);
            ShowResult(result);
        });
    }

    private void ShowResult(LogReadResult result)
    {
        _result = result;
        _allChanges = result.Changes.Select(c => new ChangeView(c)).ToList();
        ApplySearch();
        _transactionsGrid.DataSource = result.Transactions.Select(t => new TransactionView(t)).ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"{DateTime.Now:HH:mm:ss}  {result.Database}: {result.Records.Count:N0} log records read, " +
                      $"{result.Changes.Count:N0} row changes, {result.Transactions.Count:N0} transactions.");
        if (result.Analysis.FirstLsn is { } first && result.Analysis.LastLsn is { } last)
            sb.AppendLine($"LSN range {first} - {last}");
        foreach (var w in result.Warnings) sb.AppendLine("Warning: " + w);
        Log(sb.ToString().TrimEnd());

        int partial = result.Changes.Count(c => !c.IsComplete);
        SetStatus($"{result.Changes.Count:N0} row change(s) in {result.Transactions.Count:N0} transaction(s)" +
                  (partial > 0 ? $"; {partial:N0} only partially decoded (marked *)" : "") +
                  (result.Warnings.Count > 0 ? $"; {result.Warnings.Count} warning(s), see Messages" : "") + ".");
    }

    private void ApplySearch()
    {
        var text = _search.Text.Trim();
        _visibleChanges = text.Length == 0 ? _allChanges : _allChanges.Where(c => c.Matches(text)).ToList();
        _changesGrid.DataSource = _visibleChanges;
        if (_visibleChanges.Count == 0) ShowSelectedChange();
    }

    private IReadOnlyList<RowChange> SelectedChanges() =>
        _changesGrid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem).OfType<ChangeView>()
            .Select(v => v.Change).OrderBy(c => c.Lsn).ToList();

    private void ShowSelectedChange()
    {
        var change = SelectedChanges().FirstOrDefault();
        _valuesGrid.Rows.Clear();
        if (change == null)
        {
            _undoText.Text = _redoText.Text = _recordText.Text = "";
            return;
        }

        foreach (var c in change.Columns)
        {
            int i = _valuesGrid.Rows.Add(c.Name, c.Column.FullTypeName, c.Before?.DisplayText ?? "", c.After?.DisplayText ?? "");
            _valuesGrid.Rows[i].Tag = change.Operation == ChangeOperation.Update && c.IsChanged;
        }

        _undoText.Text = Lines(ScriptGenerator.Generate(change, ScriptKind.Undo));
        _redoText.Text = Lines(ScriptGenerator.Generate(change, ScriptKind.Redo));
        _recordText.Text = Lines(DescribeRecord(change));
    }

    private static string DescribeRecord(RowChange change)
    {
        var r = change.Record;
        var sb = new StringBuilder();
        sb.AppendLine($"LSN:              {r.Lsn}");
        sb.AppendLine($"Operation:        {r.Operation} ({r.Context})");
        sb.AppendLine($"Transaction:      {r.TransactionId}  '{change.Transaction.Name}'  {change.Transaction.State}");
        sb.AppendLine($"Begin / end:      {change.Transaction.BeginTime:yyyy-MM-dd HH:mm:ss.fff} / {change.Transaction.EndTime:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"User:             {change.UserName}");
        sb.AppendLine($"Object:           {r.AllocUnitName}  (allocation unit {r.AllocUnitId})");
        sb.AppendLine($"Page / slot:      {r.PageId} / {r.SlotId}");
        if (r.OffsetInRow.HasValue) sb.AppendLine($"Offset / size:    {r.OffsetInRow} / {r.ModifySize}");
        sb.AppendLine($"Source:           {r.Source}");
        if (!string.IsNullOrEmpty(r.LockInformation)) sb.AppendLine($"Locks:            {r.LockInformation}");
        foreach (var note in change.Notes) sb.AppendLine($"Note:             {note}");
        sb.AppendLine();
        for (int i = 0; i < r.Elements.Count; i++)
            sb.AppendLine($"Element {i} ({r.Elements[i].Length} bytes): 0x{Convert.ToHexString(r.Elements[i])}");
        if (r.RecordBytes != null)
        {
            sb.AppendLine();
            sb.AppendLine("Log record:");
            sb.Append(HexDump(r.RecordBytes));
        }
        return sb.ToString();
    }

    private static string HexDump(byte[] bytes)
    {
        var sb = new StringBuilder();
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            var line = bytes.AsSpan(offset, Math.Min(16, bytes.Length - offset));
            sb.Append(offset.ToString("X4", CultureInfo.InvariantCulture)).Append("  ");
            for (int i = 0; i < 16; i++)
                sb.Append(i < line.Length ? line[i].ToString("X2", CultureInfo.InvariantCulture) + " " : "   ");
            sb.Append(' ');
            foreach (var b in line) sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ export / scripts

    private IReadOnlyList<RowChange> ChangesFor(bool selectedOnly) =>
        selectedOnly ? SelectedChanges() : _visibleChanges.Select(v => v.Change).ToList();

    private void ExportChanges(string filter, string extension)
    {
        var changes = ChangesFor(selectedOnly: false);
        if (changes.Count == 0)
        {
            MessageBox.Show(this, "There are no changes to export.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new SaveFileDialog { Filter = filter, FileName = $"{SelectedDatabase}_log_changes{extension}" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ChangeExporter.ExportToFile(dialog.FileName, changes);
        SetStatus($"Exported {changes.Count:N0} change(s) to {dialog.FileName}.");
    }

    private void SaveScript(ScriptKind kind, bool selectedOnly)
    {
        var changes = ChangesFor(selectedOnly);
        if (changes.Count == 0)
        {
            MessageBox.Show(this, "There are no changes to script.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new SaveFileDialog { Filter = "SQL script|*.sql", FileName = $"{SelectedDatabase}_{kind.ToString().ToLowerInvariant()}.sql" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, ScriptGenerator.GenerateScript(changes, kind, _result?.Database), Encoding.UTF8);
        SetStatus($"Saved {kind.ToString().ToLowerInvariant()} script for {changes.Count:N0} change(s) to {dialog.FileName}.");
    }

    private void CopyScript(ScriptKind kind)
    {
        var changes = SelectedChanges();
        if (changes.Count == 0) return;
        Clipboard.SetText(ScriptGenerator.GenerateScript(changes, kind, _result?.Database));
        SetStatus($"Copied {kind.ToString().ToLowerInvariant()} script for {changes.Count:N0} change(s) to the clipboard.");
    }

    // ------------------------------------------------------------------ tools

    private void BrowseBackups()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Transaction log backups (*.trn;*.bak)|*.trn;*.bak|All files|*.*",
            Multiselect = true,
            Title = "Log backups (the paths must be readable by the SQL Server service)",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        AppendBackups(dialog.FileNames);
    }

    private void AppendBackups(IEnumerable<string> files)
    {
        var existing = _backups.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var f in files)
            if (!existing.Contains(f, StringComparer.OrdinalIgnoreCase)) existing.Add(f);
        _backups.Text = string.Join("; ", existing);
    }

    private async Task PickBackupsAsync()
    {
        var db = SelectedDatabase;
        if (db == null)
        {
            MessageBox.Show(this, "Connect and select a database first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        List<LogBackupEntry>? backups = null;
        await RunBusyAsync("Reading backup history...", async ct =>
        {
            await using var connection = await CurrentConnection().OpenAsync("master", ct);
            backups = await ServerExplorer.GetLogBackupHistoryAsync(connection, db, ct);
        });
        if (backups == null) return;
        if (backups.Count == 0)
        {
            MessageBox.Show(this, $"msdb has no transaction log backups recorded for '{db}'.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ListDialog($"Log backups of {db}", new[] { "Started", "Finished", "First LSN", "Last LSN", "File" },
            backups.Select(b => new object[] { b.StartTime, b.FinishTime, b.FirstLsn, b.LastLsn, b.PhysicalDeviceName }).ToList(),
            selectable: true);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            AppendBackups(dialog.SelectedIndexes.Select(i => backups[i].PhysicalDeviceName).Distinct());
    }

    private async Task ShowVirtualLogFilesAsync()
    {
        var db = SelectedDatabase;
        if (db == null) return;
        List<VirtualLogFile>? vlfs = null;
        await RunBusyAsync("Reading virtual log files...", async ct =>
        {
            await using var connection = await CurrentConnection().OpenAsync(db, ct);
            vlfs = await ServerExplorer.GetVirtualLogFilesAsync(connection, ct);
        });
        if (vlfs == null) return;
        using var dialog = new ListDialog($"Virtual log files of {db}", new[] { "Offset", "Size MB", "Sequence", "Active", "Status", "Parity", "First LSN" },
            vlfs.Select(v => new object[] { v.BeginOffset, v.SizeMb, v.SequenceNumber, v.IsActive, v.Status, v.Parity, v.FirstLsn }).ToList(),
            selectable: false);
        dialog.ShowDialog(this);
    }

    private async Task AttachDatabaseAsync()
    {
        using var dialog = new AttachDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunBusyAsync("Attaching database...", async ct =>
        {
            await using var connection = await CurrentConnection().OpenAsync("master", ct);
            await ServerExplorer.AttachDatabaseAsync(connection, dialog.DatabaseName, dialog.DataFile, new[] { dialog.LogFile }, ct);
            _settings.Database = dialog.DatabaseName;
        });
        await ConnectAsync();
    }

    // ------------------------------------------------------------------ helpers

    private async Task RunBusyAsync(string message, Func<CancellationToken, Task> work)
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        SetBusy(true);
        SetStatus(message);
        try
        {
            await work(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.");
        }
        catch (SqlException ex)
        {
            var text = $"SQL Server error {ex.Number}: {ex.Message}";
            if (ex.Number is 229 or 297 or 300 or 262 or 8189)
                text += "\n\nReading the transaction log requires membership in the sysadmin server role.";
            Log(text);
            SetStatus("Error: " + ex.Message);
            MessageBox.Show(this, text, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log(ex.ToString());
            SetStatus("Error: " + ex.Message);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _progress.Visible = busy;
        _connect.Enabled = !busy;
        _read.Enabled = !busy && _database.Items.Count > 0;
        _cancel.Enabled = busy;
        UseWaitCursor = busy;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void Log(string text) => _messages.AppendText(Lines(text) + Environment.NewLine);

    private static string Lines(string text) => text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
    };

    private static TextBox CreateCodeBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font("Consolas", 9.5F),
        BackColor = SystemColors.Window,
        MaxLength = 0,
    };

    private static void AddColumn(DataGridView grid, string property, string header, int width) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = property, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });

    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 0, 0) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        panel.Controls.AddRange(controls);
        return panel;
    }

    private static GroupBox Group(string title, Control content)
    {
        var box = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Padding = new Padding(6, 2, 6, 4),
        };
        box.Controls.Add(content);
        return box;
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private sealed class DatabaseItem
    {
        public DatabaseItem(DatabaseSummary summary) => Summary = summary;
        public DatabaseSummary Summary { get; }
        public override string ToString() => $"{Summary.Name}  ({Summary.RecoveryModel.ToLowerInvariant()} recovery)";
    }
}
