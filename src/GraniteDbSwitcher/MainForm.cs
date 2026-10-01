using System.Diagnostics;
using GraniteDbSwitcher.Core;
using GraniteDbSwitcher.Models;
using LogLevel = Granite.Toolkit.Core.Logging.LogLevel;

namespace GraniteDbSwitcher;

/// <summary>
/// One screen: pick the install, pick a database, Switch. Same look as the
/// install wizard (dark header, Segoe UI, auto-sizing buttons, sized from the
/// screen's working area).
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color HeaderBack = Color.FromArgb(31, 41, 55);
    private static readonly Color Accent = Color.FromArgb(56, 189, 248);

    private readonly SwitcherSettings _settings = SettingsStore.Load();
    private IReadOnlyList<GraniteInstall> _installs = Array.Empty<GraniteInstall>();
    private IReadOnlyList<DatabaseInfo> _databases = Array.Empty<DatabaseInfo>();
    private List<AppConnectionState> _currentStates = new();
    private bool _busy;

    // Install
    private readonly ComboBox _cboInstall = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _btnRefresh = MakeButton("Refresh");
    private readonly Label _lblInstall = new() { AutoSize = true, Font = new Font("Consolas", 9F), Margin = new Padding(0, 6, 0, 6) };

    // SQL
    private readonly TextBox _txtServer = new() { Width = 220 };
    private readonly ComboBox _cboAuth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly TextBox _txtUser = new() { Width = 120, PlaceholderText = "login" };
    private readonly TextBox _txtPassword = new() { Width = 120, UseSystemPasswordChar = true, PlaceholderText = "password" };
    private readonly Button _btnLoad = MakeButton("Load databases", 120);

    // Databases
    private readonly TextBox _txtFilter = new() { Width = 220, PlaceholderText = "Filter by name" };
    private readonly CheckBox _chkShowAll = new() { Text = "Show non-Granite databases", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle
    };

    // Actions
    private readonly Button _btnSwitch = MakeButton("Switch to selected database", 200);
    private readonly CheckBox _chkFixLogin = new() { Text = "Fix the apps' login access if needed", AutoSize = true, Margin = new Padding(12, 8, 0, 0) };
    private readonly Button _btnOpenWeb = MakeButton("Open Web Desktop", 120);
    private readonly Button _btnOpenProcess = MakeButton("Open Process App", 120);
    private readonly Button _btnLogs = MakeButton("Log folder", 90);

    private readonly RichTextBox _log = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.FromArgb(17, 24, 39),
        ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9F),
        BorderStyle = BorderStyle.None
    };

    public static string VersionLabel
    {
        get
        {
            var version = typeof(MainForm).Assembly.GetName().Version;
            return version is null ? "dev build" : $"v{version.ToString(3)}";
        }
    }

    public MainForm()
    {
        Text = $"GraniteWMS DB Switcher ({VersionLabel})";
        StartPosition = FormStartPosition.CenterScreen;
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
        Size = new Size(Math.Clamp((int)(workingArea.Width * 0.75), 900, 1200),
                        Math.Clamp((int)(workingArea.Height * 0.85), 680, 950));
        MinimumSize = new Size(880, 640);
        Font = new Font("Segoe UI", 9F);

        _btnSwitch.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        _cboAuth.Items.AddRange(new object[] { "Windows authentication", "SQL Server login" });
        _cboAuth.SelectedIndex = _settings.SqlAuth == SqlAuthMode.SqlLogin ? 1 : 0;
        _txtUser.Text = _settings.SqlUser ?? string.Empty;
        _chkShowAll.Checked = _settings.ShowAllDatabases;
        _chkFixLogin.Checked = _settings.FixLoginAccess;

        BuildGridColumns();
        BuildLayout();
        UpdateAuthFields();
        UpdateButtons();

        _cboInstall.SelectedIndexChanged += (_, _) => OnInstallChanged();
        _btnRefresh.Click += async (_, _) => await DiscoverAsync();
        _cboAuth.SelectedIndexChanged += (_, _) => UpdateAuthFields();
        _btnLoad.Click += async (_, _) => await LoadDatabasesAsync();
        _txtFilter.TextChanged += (_, _) => FillGrid();
        _chkShowAll.CheckedChanged += (_, _) => { _settings.ShowAllDatabases = _chkShowAll.Checked; FillGrid(); };
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await SwitchAsync(); };
        _btnSwitch.Click += async (_, _) => await SwitchAsync();
        _btnOpenWeb.Click += (_, _) => OpenApp(GraniteAppKind.WebDesktop);
        _btnOpenProcess.Click += (_, _) => OpenApp(GraniteAppKind.ProcessApp);
        _btnLogs.Click += (_, _) =>
        {
            Directory.CreateDirectory(LogFile.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogFile.Folder}\"") { UseShellExecute = true });
        };

        Shown += async (_, _) => await DiscoverAsync();
        FormClosing += OnClosing;
    }

    // ---------------------------------------------------------------- layout

    private static Button MakeButton(string text, int minWidth = 90) => new()
    {
        Text = text,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowOnly,
        MinimumSize = new Size(minWidth, 30),
        Margin = new Padding(0, 0, 6, 0)
    };

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 7, 8, 0)
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6), Dock = DockStyle.Fill };
        row.Controls.AddRange(controls);
        return row;
    }

    private void BuildGridColumns()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Current", HeaderText = "", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Database", HeaderText = "Database", FillWeight = 120 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Version", HeaderText = "Granite version", FillWeight = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Suits", HeaderText = "Suits this install", FillWeight = 50 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = "Size (MB)", FillWeight = 35, DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Restored", HeaderText = "Last restored", FillWeight = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Note", HeaderText = "Note", FillWeight = 110 });
    }

    private void BuildLayout()
    {
        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Padding = new Padding(16, 0, 0, 0),
            BackColor = HeaderBack,
            ForeColor = Color.White,
            Text = "GraniteWMS DB Switcher: point an install at another database"
        };
        var accentBar = new Panel { Dock = DockStyle.Top, Height = 3, BackColor = Accent };

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(16, 12, 16, 0)
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var installRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 4) };
        installRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        installRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        installRow.Controls.Add(_cboInstall, 0, 0);
        installRow.Controls.Add(_btnRefresh, 1, 0);
        _btnRefresh.Margin = new Padding(6, 0, 0, 0);

        top.Controls.Add(MakeLabel("Granite install"), 0, 0);
        top.Controls.Add(installRow, 1, 0);
        top.Controls.Add(new Label(), 0, 1);
        top.Controls.Add(_lblInstall, 1, 1);
        top.Controls.Add(MakeLabel("SQL Server"), 0, 2);
        top.Controls.Add(Row(_txtServer, _cboAuth, _txtUser, _txtPassword, _btnLoad), 1, 2);
        top.Controls.Add(MakeLabel("Databases"), 0, 3);
        top.Controls.Add(Row(_txtFilter, _chkShowAll), 1, 3);
        foreach (Control c in new Control[] { _txtServer, _cboAuth, _txtUser, _txtPassword, _txtFilter })
            c.Margin = new Padding(0, 3, 6, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(16, 8, 16, 8)
        };
        actions.Controls.AddRange(new Control[] { _btnSwitch, _chkFixLogin, new Label { Width = 24 }, _btnOpenWeb, _btnOpenProcess, _btnLogs });

        var gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 4, 16, 0) };
        gridHost.Controls.Add(_grid);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2
        };
        split.Panel1.Controls.Add(gridHost);
        split.Panel1.Controls.Add(actions);
        split.Panel2.Controls.Add(_log);
        split.Panel2.Padding = new Padding(0);

        Controls.Add(split);
        Controls.Add(top);
        Controls.Add(accentBar);
        Controls.Add(header);

        Load += (_, _) =>
        {
            try { split.SplitterDistance = Math.Max(200, split.Height - 190); } catch { /* tiny window */ }
        };
    }

    private void UpdateAuthFields()
    {
        bool sql = _cboAuth.SelectedIndex == 1;
        _txtUser.Visible = sql;
        _txtPassword.Visible = sql;
    }

    private GraniteInstall? SelectedInstall => _cboInstall.SelectedItem as GraniteInstall;

    private DatabaseInfo? SelectedDatabase =>
        _grid.SelectedRows.Count == 1 ? _grid.SelectedRows[0].Tag as DatabaseInfo : null;

    private string? CurrentDatabase =>
        _currentStates.Select(s => s.Database).FirstOrDefault(d => !string.IsNullOrEmpty(d));

    private void UpdateButtons()
    {
        var install = SelectedInstall;
        var db = SelectedDatabase;
        foreach (Control c in new Control[] { _cboInstall, _btnRefresh, _btnLoad, _txtServer, _cboAuth, _txtUser, _txtPassword, _grid, _chkFixLogin })
            c.Enabled = !_busy;
        _btnSwitch.Enabled = !_busy && install is not null && db is not null && db.IsGranite
                             && !string.Equals(db.Name, CurrentDatabase, StringComparison.OrdinalIgnoreCase);
        _btnOpenWeb.Enabled = install?.Get(GraniteAppKind.WebDesktop)?.LocalUrl is not null;
        _btnOpenProcess.Enabled = install?.Get(GraniteAppKind.ProcessApp)?.LocalUrl is not null;
        UseWaitCursor = _busy;
    }

    // ------------------------------------------------------------------- log

    private void Log(LogEntry entry)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(entry)); return; }
        LogFile.Append(entry);
        Color color = entry.Level switch
        {
            LogLevel.Success => Color.FromArgb(134, 239, 172),
            LogLevel.Warning => Color.FromArgb(253, 224, 71),
            LogLevel.Error => Color.FromArgb(252, 165, 165),
            LogLevel.Detail => Color.Gray,
            LogLevel.Stage => Accent,
            _ => Color.Gainsboro
        };
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.AppendText($"{entry.Timestamp:HH:mm:ss}  {(entry.Level == LogLevel.Stage ? "== " : "")}{entry.Message}{Environment.NewLine}");
        _log.ScrollToCaret();
    }

    private void Log(LogLevel level, string message) => Log(new LogEntry(level, message));

    // ------------------------------------------------------------- installs

    private async Task DiscoverAsync()
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        try
        {
            _installs = await IisService.DiscoverAsync(CancellationToken.None);
            _cboInstall.Items.Clear();
            foreach (var i in _installs) _cboInstall.Items.Add(i);

            if (_installs.Count == 0)
            {
                Log(LogLevel.Warning, "No Granite sites found in IIS (looked for folders holding the Business API, Custodian, Process App or Web Desktop).");
                _lblInstall.Text = "No Granite install found.";
                return;
            }

            Log(LogLevel.Info, $"Found {_installs.Count} Granite install{(_installs.Count == 1 ? "" : "s")} in IIS.");
            var last = _installs.FirstOrDefault(i => i.RootFolder.Equals(_settings.LastInstall, StringComparison.OrdinalIgnoreCase));
            _cboInstall.SelectedItem = last ?? _installs[0];
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }

        if (SelectedInstall is not null && _cboAuth.SelectedIndex == 0)
            await LoadDatabasesAsync();
    }

    private void OnInstallChanged()
    {
        var install = SelectedInstall;
        if (install is null) return;
        _settings.LastInstall = install.RootFolder;
        string previousServer = _txtServer.Text;
        RefreshInstallInfo();

        string? server = _currentStates.Select(s => s.Server).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (server is not null) _txtServer.Text = server;

        bool sameServer = AppConnectionReader.NormaliseServer(previousServer) == AppConnectionReader.NormaliseServer(_txtServer.Text);
        if (!sameServer) { _databases = Array.Empty<DatabaseInfo>(); }
        FillGrid();
        UpdateButtons();
    }

    private void RefreshInstallInfo()
    {
        var install = SelectedInstall;
        if (install is null) return;
        _currentStates = install.DatabaseApps.Select(AppConnectionReader.Read).ToList();

        var lines = new List<string>();
        foreach (var app in install.Apps)
        {
            string url = app.LocalUrl ?? "(no binding)";
            var state = _currentStates.FirstOrDefault(s => s.App == app);
            string db = state is null ? "" :
                state.Problem is not null ? $"  !! {state.Problem}" :
                $"  -> {state.Database} on {state.Server}";
            lines.Add($"{app.Title,-13}{url,-30}{db}");
        }
        _lblInstall.Text = string.Join(Environment.NewLine, lines);

        var dbs = _currentStates.Where(s => s.Problem is null).Select(s => s.Database ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dbs.Count > 1)
            Log(LogLevel.Warning, $"The apps in {install.RootFolder} point at different databases ({string.Join(", ", dbs)}). Switching puts them all on one.");
        var servers = _currentStates.Where(s => s.Problem is null).Select(s => AppConnectionReader.NormaliseServer(s.Server)).Distinct().ToList();
        if (servers.Count > 1)
            Log(LogLevel.Warning, $"The apps in {install.RootFolder} point at different SQL Servers; the switcher only changes the database name, not the server.");
    }

    // ------------------------------------------------------------ databases

    private SqlAdminIdentity AdminIdentity() => new(
        _txtServer.Text.Trim(),
        _cboAuth.SelectedIndex == 1 ? SqlAuthMode.SqlLogin : SqlAuthMode.Windows,
        _txtUser.Text.Trim(),
        _txtPassword.Text);

    private async Task LoadDatabasesAsync()
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_txtServer.Text))
        {
            Log(LogLevel.Warning, "Enter the SQL Server first.");
            return;
        }
        _busy = true;
        UpdateButtons();
        try
        {
            _databases = await DatabaseCatalog.ListAsync(AdminIdentity(), CancellationToken.None);
            int granite = _databases.Count(d => d.IsGranite);
            Log(LogLevel.Info, $"{_txtServer.Text.Trim()}: {granite} Granite database{(granite == 1 ? "" : "s")} ({_databases.Count} in all).");
            _settings.SqlAuth = _cboAuth.SelectedIndex == 1 ? SqlAuthMode.SqlLogin : SqlAuthMode.Windows;
            _settings.SqlUser = _txtUser.Text.Trim();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Couldn't list databases on {_txtServer.Text.Trim()}: {ex.Message}");
        }
        finally
        {
            _busy = false;
            FillGrid();
            UpdateButtons();
        }
    }

    private void FillGrid()
    {
        string? keepSelected = SelectedDatabase?.Name;
        var install = SelectedInstall;
        string? current = CurrentDatabase;
        string filter = _txtFilter.Text.Trim();

        _grid.Rows.Clear();
        foreach (var db in _databases)
        {
            if (!_chkShowAll.Checked && !db.IsGranite && db.Problem is null) continue;
            if (filter.Length > 0 && !db.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            bool isCurrent = string.Equals(db.Name, current, StringComparison.OrdinalIgnoreCase);
            var compat = install is null ? Compatibility.Unknown : GraniteVersionRules.Check(install, db.Version);
            string suits = !db.IsGranite ? "" : compat switch
            {
                Compatibility.Match => "Yes",
                Compatibility.Mismatch => $"No (install is V{install!.AppVersion!.Major})",
                _ => "Unknown"
            };
            string note = db.Problem ?? (!db.IsGranite ? "Not a Granite database" : db.Version?.Source ?? "No version marker found");

            int idx = _grid.Rows.Add(
                isCurrent ? "●" : "",
                db.Name,
                db.Version is null ? (db.IsGranite ? "?" : "") : "V" + db.Version.Label,
                suits,
                db.SizeMb is null ? "" : db.SizeMb.Value.ToString("N0"),
                db.LastRestored?.ToString("yyyy-MM-dd HH:mm") ?? "",
                note);
            var row = _grid.Rows[idx];
            row.Tag = db;
            if (isCurrent) row.DefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
            if (!db.IsGranite) row.DefaultCellStyle.ForeColor = Color.Gray;
            else if (compat == Compatibility.Mismatch) row.Cells["Suits"].Style.ForeColor = Color.Firebrick;
            else if (compat == Compatibility.Match) row.Cells["Suits"].Style.ForeColor = Color.ForestGreen;
        }

        _grid.ClearSelection();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is DatabaseInfo d && d.Name.Equals(keepSelected, StringComparison.OrdinalIgnoreCase))
            {
                row.Selected = true;
                break;
            }
        }
        UpdateButtons();
    }

    // --------------------------------------------------------------- switch

    private async Task SwitchAsync()
    {
        var install = SelectedInstall;
        var db = SelectedDatabase;
        if (_busy || install is null || db is null) return;
        if (!db.IsGranite)
        {
            MessageBox.Show(this, $"{db.Name} isn't a Granite database (it has no SystemSettings table).", "Not a Granite database",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (string.Equals(db.Name, CurrentDatabase, StringComparison.OrdinalIgnoreCase)) return;

        var compat = GraniteVersionRules.Check(install, db.Version);
        if (compat == Compatibility.Mismatch)
        {
            var answer = MessageBox.Show(this,
                $"{db.Name} is a V{db.Version!.Label} database, but the apps in {install.RootFolder} are V{install.AppVersion!.Major}.{install.AppVersion.Minor}.\n\n" +
                "The apps will probably fail against it. Switch anyway?",
                "Version mismatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }
        else if (compat == Compatibility.Unknown)
        {
            var answer = MessageBox.Show(this,
                $"The switcher couldn't tell which Granite version {db.Name} is (or which the install is). Switch anyway?",
                "Version unknown", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        _settings.FixLoginAccess = _chkFixLogin.Checked;
        _busy = true;
        UpdateButtons();
        try
        {
            var runner = new SwitchRunner(Log);
            await runner.RunAsync(install, AdminIdentity(), db.Name,
                new SwitchOptions { FixLoginAccess = _chkFixLogin.Checked }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, ex.Message);
        }
        finally
        {
            _busy = false;
            RefreshInstallInfo();
            FillGrid();
            UpdateButtons();
            SettingsStore.Save(_settings);
        }
    }

    private void OpenApp(GraniteAppKind kind)
    {
        string? url = SelectedInstall?.Get(kind)?.LocalUrl;
        if (url is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Couldn't open {url}: {ex.Message}");
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy)
        {
            var r = MessageBox.Show(this, "A switch is still running. Close anyway?", "Switch in progress",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (r == DialogResult.No) { e.Cancel = true; return; }
        }
        _settings.SqlAuth = _cboAuth.SelectedIndex == 1 ? SqlAuthMode.SqlLogin : SqlAuthMode.Windows;
        _settings.SqlUser = _txtUser.Text.Trim();
        _settings.FixLoginAccess = _chkFixLogin.Checked;
        SettingsStore.Save(_settings);
    }
}
