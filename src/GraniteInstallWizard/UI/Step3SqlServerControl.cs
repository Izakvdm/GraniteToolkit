using System.Security.Cryptography;
using GraniteInstallWizard.Core;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.UI;

public sealed class Step3SqlServerControl : WizardStepControl
{
    private readonly ComboBox _cmbServer = new()
    {
        Width = 300,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems
    };
    private readonly Button _btnRefreshServers = MakeButton("Refresh", 90, new Padding(6, 0, 0, 0));

    private readonly RadioButton _rbWindows = new() { Text = "Windows Authentication (the account this wizard is running as)", AutoSize = true, Checked = true };
    private readonly RadioButton _rbSql = new() { Text = "SQL Server login", AutoSize = true };
    private readonly TextBox _txtAdminUser = new() { Width = 200 };
    private readonly TextBox _txtAdminPassword = new() { Width = 200, UseSystemPasswordChar = true };

    private readonly Button _btnTest = MakeButton("Test Connection", 150, new Padding(0, 12, 0, 0));
    /// <summary>
    /// One label per result line, each in its own colour. v0.3.0 used a
    /// single label coloured by the worst line, so one blocking problem
    /// turned "Connected" and every informational line red as well.
    /// </summary>
    private readonly FlowLayoutPanel _results = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 8) };

    private readonly RadioButton _rbCreateNew = new() { Text = "Create a new, clean database (runs GraniteDatabase_Create.sql)", AutoSize = true, Checked = true };
    private readonly RadioButton _rbUseExisting = new() { Text = "Use an existing Granite database (keeps its data; the create script is not run)", AutoSize = true };
    private readonly ComboBox _cmbDatabase = new()
    {
        Width = 300,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems
    };
    private readonly Label _lblDatabaseHint = new() { AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 8) };
    private readonly TextBox _txtAppLogin = new() { Width = 200 };
    private readonly TextBox _txtAppPassword = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly TextBox _txtAppPassword2 = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly Button _btnGenerate = MakeButton("Generate", 90, new Padding(6, 0, 0, 0));
    private readonly CheckBox _chkShow = new() { Text = "Show", AutoSize = true, Margin = new Padding(6, 4, 0, 0) };
    private readonly CheckBox _chkResetPassword = new()
    {
        Text = "If this login already exists with a different password, change its password to this one (anything else using the login will need the new password)",
        AutoSize = true,
        MaximumSize = new Size(640, 0),
        ForeColor = Color.DarkOrange,
        Margin = new Padding(0, 0, 0, 4)
    };

    private readonly CheckBox _chkDrop = new()
    {
        Text = "Drop and recreate the database if it already exists (every row in it is lost)",
        AutoSize = true,
        ForeColor = Color.Firebrick,
        Margin = new Padding(0, 12, 0, 0)
    };
    private readonly CheckBox _chkDbHotfix = new() { Text = "Run the Hotfix database scripts", AutoSize = true };
    /// <summary>False when the release has no Hotfix database scripts (V7.0): the box is then disabled and off.</summary>
    private bool _hasDbHotfix = true;
    private readonly CheckBox _chkHotfix = new() { Text = "Apply the Hotfix app files", AutoSize = true };
    private readonly TextBox _txtTokenFile = new() { Width = 420, ReadOnly = true };
    private readonly Button _btnTokenBrowse = MakeButton("Browse...", 90, new Padding(6, 0, 0, 0));
    private readonly Button _btnTokenClear = MakeButton("Clear", 70, new Padding(6, 0, 0, 0));
    private readonly Label _lblTokenHint = new() { AutoSize = true, MaximumSize = new Size(620, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 4, 0, 8) };

    private InstallContext? _context;

    /// <summary>True while OnEnter copies the context into the controls, so change handlers don't treat that as user input.</summary>
    private bool _loading;

    /// <summary>The user's explicit hotfix-scripts choice, or null for the database mode's default.</summary>
    private bool? _dbHotfixChoice;

    /// <summary>The app password that passed the existing-login check, if the login exists.</summary>
    private string? _appLoginVerifiedFor;

    public override string StepTitle => "Step 3 of 6: SQL Server";

    public Step3SqlServerControl()
    {
        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));

        page.Controls.Add(MakeFieldLabel("SQL Server / instance name:"));
        page.Controls.Add(MakeRow(_cmbServer, _btnRefreshServers));
        page.Controls.Add(MakeHint("Pick a detected instance or type one, e.g. .\\SQLEXPRESS or SQL01\\GRANITE. Detection is best-effort: an instance with the SQL Browser service off won't be listed but can still be typed."));

        page.Controls.Add(MakeFieldLabel("Connect as (needs sysadmin, to create the database, login and SQLCLR assembly):"));
        page.Controls.Add(_rbWindows);
        page.Controls.Add(_rbSql);
        page.Controls.Add(MakeRow(MakeFieldLabel("User:"), _txtAdminUser, MakeFieldLabel("  Password:"), _txtAdminPassword));
        page.Controls.Add(_btnTest);
        page.Controls.Add(_results);

        page.Controls.Add(MakeFieldLabel("Granite database:"));
        // Own container: radio buttons group by parent, and the two
        // authentication radios above share the page.
        var modeGroup = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        modeGroup.Controls.Add(_rbCreateNew);
        modeGroup.Controls.Add(_rbUseExisting);
        page.Controls.Add(modeGroup);
        page.Controls.Add(MakeRow(MakeFieldLabel("Name:"), _cmbDatabase));
        page.Controls.Add(_lblDatabaseHint);

        page.Controls.Add(MakeFieldLabel("SQL login the Granite apps use (created if it doesn't exist, made db_owner of the database):"));
        page.Controls.Add(MakeRow(MakeFieldLabel("Login:"), _txtAppLogin));
        page.Controls.Add(MakeRow(MakeFieldLabel("Password:"), _txtAppPassword, _btnGenerate, _chkShow));
        page.Controls.Add(MakeRow(MakeFieldLabel("Confirm:"), _txtAppPassword2));
        page.Controls.Add(MakeHint("This password goes into each app's appsettings.json. Write it down: the wizard never saves passwords to a profile."));
        page.Controls.Add(_chkResetPassword);

        page.Controls.Add(_chkDrop);
        page.Controls.Add(_chkDbHotfix);
        page.Controls.Add(_chkHotfix);
        page.Controls.Add(MakeFieldLabel("Custodian token (Custodian.md from Granite):"));
        page.Controls.Add(MakeRow(_txtTokenFile, _btnTokenBrowse, _btnTokenClear));
        page.Controls.Add(_lblTokenHint);
        Controls.Add(page);

        foreach (string name in SqlInstanceDiscovery.GetLocalInstances())
            if (!_cmbServer.Items.Contains(name)) _cmbServer.Items.Add(name);
        _ = RefreshServersAsync();

        _btnRefreshServers.Click += async (_, _) => await RefreshServersAsync();
        _rbWindows.CheckedChanged += (_, _) => UpdateAuthFields();
        _rbSql.CheckedChanged += (_, _) => UpdateAuthFields();
        _btnTest.Click += async (_, _) => await TestAsync();
        _btnTokenBrowse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog { Title = "Custodian.md from Granite", Filter = "Custodian token (*.md)|*.md|All files (*.*)|*.*", CheckFileExists = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                CustodianToken.Parse(File.ReadAllText(dlg.FileName), Path.GetFileName(dlg.FileName));
                _txtTokenFile.Text = dlg.FileName;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, "Not a usable Custodian.md", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            DescribeToken();
        };
        _btnTokenClear.Click += (_, _) => { _txtTokenFile.Text = ""; DescribeToken(); };
        _rbCreateNew.CheckedChanged += (_, _) => { if (_rbCreateNew.Checked) OnModeChanged(); };
        _rbUseExisting.CheckedChanged += (_, _) => { if (_rbUseExisting.Checked) OnModeChanged(); };
        _chkDbHotfix.CheckedChanged += (_, _) => { if (!_loading) _dbHotfixChoice = _chkDbHotfix.Checked; };
        _btnGenerate.Click += (_, _) =>
        {
            string pwd = GeneratePassword();
            _txtAppPassword.Text = pwd;
            _txtAppPassword2.Text = pwd;
            _chkShow.Checked = true;
        };
        _chkShow.CheckedChanged += (_, _) =>
        {
            _txtAppPassword.UseSystemPasswordChar = !_chkShow.Checked;
            _txtAppPassword2.UseSystemPasswordChar = !_chkShow.Checked;
        };
    }

    /// <summary>
    /// 20 characters from a set without quotes, semicolons or other
    /// characters that need escaping in a connection string or JSON.
    /// </summary>
    private static string GeneratePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789!@#%^*-_+";
        return new string(Enumerable.Range(0, 20).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }

    /// <summary>Best-effort: fills the database dropdown with the Granite databases on the server.</summary>
    private async Task RefreshDatabaseListAsync()
    {
        if (_context is null) return;
        try
        {
            var names = await SqlServerInspector.ListGraniteDatabasesAsync(_context, CancellationToken.None);
            string current = _cmbDatabase.Text;
            _cmbDatabase.Items.Clear();
            foreach (string n in names) _cmbDatabase.Items.Add(n);
            _cmbDatabase.Text = current;
        }
        catch { /* the list is a convenience; typing a name still works */ }
    }

    private async Task RefreshServersAsync()
    {
        _btnRefreshServers.Enabled = false;
        try
        {
            foreach (string name in SqlInstanceDiscovery.GetLocalInstances().Concat(await SqlInstanceDiscovery.DiscoverNetworkInstancesAsync()))
                if (!_cmbServer.Items.Contains(name)) _cmbServer.Items.Add(name);
        }
        finally { _btnRefreshServers.Enabled = true; }
    }

    /// <summary>
    /// A deliberate switch between "new" and "existing" drops any earlier
    /// hotfix-scripts choice and goes back to the mode's default: on for a
    /// new database (part of a V6.0 install), off for an existing one (it
    /// changes objects in a database that may be live).
    /// </summary>
    private void OnModeChanged()
    {
        if (!_loading) _dbHotfixChoice = null;
        UpdateModeFields();
    }

    private void UpdateModeFields()
    {
        bool isNew = _rbCreateNew.Checked;
        bool wasLoading = _loading;
        _loading = true;
        _chkDbHotfix.Checked = _hasDbHotfix && (_dbHotfixChoice ?? isNew);
        _chkDbHotfix.Enabled = _hasDbHotfix;
        _loading = wasLoading;
        _chkDrop.Enabled = isNew;
        if (!isNew) _chkDrop.Checked = false;
        _lblDatabaseHint.Text = isNew
            ? "A new database with this name is created. If one already exists the install stops, unless \"Drop and recreate\" is ticked."
            : "Pick from the list (filled after Test Connection with the databases on this server that have Granite's tables) or type a name. Its data is left as it is; the app login is made db_owner of it.";
    }

    private void UpdateAuthFields()
    {
        _txtAdminUser.Enabled = _rbSql.Checked;
        _txtAdminPassword.Enabled = _rbSql.Checked;
    }

    private async Task TestAsync()
    {
        if (_context is null) return;
        OnLeave(_context);
        if (string.IsNullOrWhiteSpace(_context.SqlServer)) { ShowLines(("Enter the SQL Server first.", Color.Firebrick)); return; }

        _btnTest.Enabled = false;
        ShowLines(("Connecting...", Color.DimGray));
        try
        {
            SqlServerInfo info = await SqlServerInspector.InspectAsync(_context, CancellationToken.None);
            await RefreshDatabaseListAsync();
            _context.LastSqlCheck = info;
            _context.LastSqlCheckKey = _context.SqlCheckKey;

            var lines = new List<(string, Color)> { ($"Connected: SQL Server {info.Version}, as {info.ConnectedAs}.", Color.SeaGreen) };
            lines.AddRange(SqlServerInspector.Blockers(info, _context).Select(b => (b, Color.Firebrick)));
            lines.AddRange(SqlServerInspector.Warnings(info, _context).Select(w => (w, Color.DarkOrange)));

            _appLoginVerifiedFor = null;
            if (info.AppLoginExists)
            {
                if (string.IsNullOrEmpty(_context.AppPassword))
                {
                    lines.Add(($"Login {_context.AppLogin} already exists: enter its current password below and test again.", Color.Firebrick));
                }
                else
                {
                    var (ok, message) = await SqlServerInspector.CheckExistingAppLoginAsync(_context, CancellationToken.None);
                    Color color = ok ? Color.SeaGreen : _context.ResetExistingAppLoginPassword ? Color.DarkOrange : Color.Firebrick;
                    lines.Add((message, color));
                    if (ok) _appLoginVerifiedFor = _context.AppPassword;
                }
            }
            ShowLines(lines.ToArray());
        }
        catch (Exception ex)
        {
            _context.LastSqlCheck = null;
            _context.LastSqlCheckKey = null;
            ShowLines(($"Could not connect: {ex.Message}", Color.Firebrick));
        }
        finally { _btnTest.Enabled = true; }
    }

    private void ShowLines(params (string Text, Color Color)[] lines)
    {
        _results.SuspendLayout();
        _results.Controls.Clear();
        foreach (var (text, color) in lines)
            _results.Controls.Add(new Label { Text = text, ForeColor = color, AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 3) });
        _results.ResumeLayout();
    }

    public override void OnEnter(InstallContext context)
    {
        _context = context;
        _loading = true;
        _dbHotfixChoice = context.DatabaseHotfixChoice;
        _cmbServer.Text = context.SqlServer;
        _rbWindows.Checked = context.SqlAuth == SqlAuthMode.Windows;
        _rbSql.Checked = context.SqlAuth == SqlAuthMode.SqlLogin;
        _txtAdminUser.Text = context.SqlAdminUser;
        _txtAdminPassword.Text = context.SqlAdminPassword;
        _rbUseExisting.Checked = context.DatabaseMode == DatabaseMode.UseExisting;
        _rbCreateNew.Checked = context.DatabaseMode == DatabaseMode.CreateNew;
        _cmbDatabase.Text = context.DatabaseName;
        _txtAppLogin.Text = context.AppLogin;
        _txtAppPassword.Text = context.AppPassword;
        _txtAppPassword2.Text = context.AppPassword;
        _chkDrop.Checked = context.DropExistingDatabase;
        _chkHotfix.Checked = context.ApplyHotfix;
        _chkResetPassword.Checked = context.ResetExistingAppLoginPassword;
        _txtTokenFile.Text = context.CustodianTokenFile;
        DescribeHotfix(context);
        DescribeToken();
        UpdateAuthFields();
        UpdateModeFields(); // sets the hotfix-scripts box from the choice or the mode's default
        _loading = false;
    }

    /// <summary>
    /// Names what the two Hotfix boxes would actually do with this release
    /// (v0.5.0: V6.0 and V7.0 ship different Hotfix folders).
    /// </summary>
    private void DescribeHotfix(InstallContext context)
    {
        var scripts = HotfixScripts.Find(context);
        _hasDbHotfix = scripts.Count > 0;
        _chkDbHotfix.Text = $"Run the Hotfix database scripts ({HotfixScripts.Describe(scripts)})";

        var appFolders = GraniteComponent.CoreStack
            .Select(comp => (comp, path: context.HotfixPathFor(comp)))
            .Where(x => x.path is not null)
            .Select(x => x.comp.Title)
            .ToList();
        _chkHotfix.Text = appFolders.Count == 0
            ? "Apply the Hotfix app files (none in this release)"
            : $"Apply the Hotfix app files (updated {string.Join(", ", appFolders)} files)";
        _chkHotfix.Enabled = appFolders.Count > 0;
        if (appFolders.Count == 0) _chkHotfix.Checked = false;
    }

    private void DescribeToken()
    {
        if (_context is null) return;
        bool custodian = _context.IsEnabled(GraniteComponent.Custodian);
        string release = Path.Combine(_context.HotfixRoot, "Custodian.md");
        _lblTokenHint.Text = !custodian
            ? "Custodian isn't being installed, so no token is needed."
            : _txtTokenFile.Text.Length > 0
                ? "This file's token is written to the database, replacing any token already there."
                : File.Exists(release)
                    ? "Using the release's Hotfix\\Custodian.md. Pick a newer file from Granite if Custodian reports \"Bad credentials\"."
                    : CustodianToken.NoSourceText;
        _txtTokenFile.Enabled = _btnTokenBrowse.Enabled = _btnTokenClear.Enabled = custodian;
    }

    public override void OnLeave(InstallContext context)
    {
        context.CustodianTokenFile = _txtTokenFile.Text;
        context.SqlServer = _cmbServer.Text.Trim();
        context.SqlAuth = _rbSql.Checked ? SqlAuthMode.SqlLogin : SqlAuthMode.Windows;
        context.SqlAdminUser = _txtAdminUser.Text.Trim();
        context.SqlAdminPassword = _txtAdminPassword.Text;
        context.DatabaseMode = _rbUseExisting.Checked ? DatabaseMode.UseExisting : DatabaseMode.CreateNew;
        context.DatabaseName = _cmbDatabase.Text.Trim();
        context.AppLogin = _txtAppLogin.Text.Trim();
        context.AppPassword = _txtAppPassword.Text;
        context.DropExistingDatabase = _chkDrop.Checked && !_rbUseExisting.Checked;
        context.ApplyHotfix = _chkHotfix.Checked;
        context.DatabaseHotfixChoice = _dbHotfixChoice;
        context.ResetExistingAppLoginPassword = _chkResetPassword.Checked;
    }

    public override bool ValidateStep(InstallContext context, out string error)
    {
        OnLeave(context);
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(context.SqlServer)) { error = "Enter the SQL Server / instance name."; return false; }
        if (context.SqlAuth == SqlAuthMode.SqlLogin && string.IsNullOrWhiteSpace(context.SqlAdminUser)) { error = "Enter the SQL login to connect as."; return false; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(context.DatabaseName, @"^[A-Za-z_][A-Za-z0-9_\-]{0,100}$"))
        { error = "Use a plain database name: letters, digits, _ and -, starting with a letter."; return false; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(context.AppLogin, @"^[A-Za-z_][A-Za-z0-9_\-\.]{0,100}$"))
        { error = "Use a plain login name: letters, digits, _ - and ., starting with a letter."; return false; }
        if (string.IsNullOrEmpty(context.AppPassword)) { error = "Enter a password for the app login (or click Generate)."; return false; }
        if (context.AppPassword != _txtAppPassword2.Text) { error = "The two app login passwords don't match."; return false; }

        if (context.IsEnabled(GraniteComponent.Custodian) && context.CustodianTokenFile.Length > 0)
        {
            try { CustodianToken.Parse(File.ReadAllText(context.CustodianTokenFile), Path.GetFileName(context.CustodianTokenFile)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { error = $"Custodian token file: {ex.Message}"; return false; }
        }

        if (context.LastSqlCheck is null || context.LastSqlCheckKey != context.SqlCheckKey)
        { error = "Click Test Connection first. It has to pass for the settings currently entered."; return false; }

        var blockers = SqlServerInspector.Blockers(context.LastSqlCheck, context);
        if (blockers.Count > 0) { error = string.Join(Environment.NewLine + Environment.NewLine, blockers); return false; }

        if (context.LastSqlCheck.AppLoginExists && _appLoginVerifiedFor != context.AppPassword)
        {
            if (!context.ResetExistingAppLoginPassword)
            {
                error = $"Login {context.AppLogin} already exists and the password entered hasn't been confirmed for it. Enter its current password and click Test Connection, or tick \"change its password\" to replace it.";
                return false;
            }
            var answer = MessageBox.Show(this,
                $"Login {context.AppLogin} already exists. If the password entered doesn't match, the install will CHANGE its password.\n\nAnything else using {context.AppLogin} (an older Granite install, an integration) will stop working until it's given the new password.\n\nContinue with this setting?",
                "Change existing login password", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) { error = "Untick \"change its password\" or enter the login's current password."; return false; }
        }

        if (context.DatabaseMode == DatabaseMode.CreateNew && context.LastSqlCheck.DatabaseExists && context.DropExistingDatabase)
        {
            var answer = MessageBox.Show(this,
                $"Database {context.DatabaseName} on {context.SqlServer} will be DROPPED during the install, and every row in it lost.\n\nContinue with this setting?",
                "Drop existing database", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) { error = "Untick \"Drop and recreate\" or choose another database name."; return false; }
        }
        return true;
    }
}
