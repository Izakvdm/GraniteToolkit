using GraniteAttachInstaller.Core;
using GraniteAttachInstaller.Models;

namespace GraniteAttachInstaller.UI;

public sealed class Step1DatabaseControl : WizardStepControl
{
    public override string StepTitle => "Step 1 of 3: SQL Server and database";

    private readonly ComboBox _cboServer = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly RadioButton _rbWindows = new() { Text = "Windows authentication (this account)", AutoSize = true, Checked = true };
    private readonly RadioButton _rbSql = new() { Text = "SQL login", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly TextBox _txtSqlUser = new() { Width = 200, Enabled = false };
    private readonly TextBox _txtSqlPassword = new() { Width = 200, Enabled = false, UseSystemPasswordChar = true };
    private readonly ComboBox _cboDatabase = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _btnListDatabases = MakeButton("List databases");
    private readonly Button _btnTest = MakeButton("Test Connection", 130);
    private readonly Label _lblResult = new() { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };

    public Step1DatabaseControl()
    {
        var page = MakePage();

        page.Controls.Add(MakeHeading(StepTitle));
        page.Controls.Add(new Label { Text = "Where should Granite Attach's database objects live? Point this at whichever GraniteWMS install and database you're setting it up for - a new one from the Install Wizard, or an existing one like GraniteLive.", AutoSize = true, MaximumSize = new Size(680, 0), Margin = new Padding(0, 0, 0, 12) });

        page.Controls.Add(MakeFieldLabel("SQL Server instance"));
        page.Controls.Add(_cboServer);
        page.Controls.Add(MakeHint("Pick from the list, or type one in (e.g. NEWSERVER\\SQLEXPRESS). The list is best-effort - your instance not showing up here doesn't mean it's unreachable."));

        page.Controls.Add(MakeFieldLabel("Authentication"));
        page.Controls.Add(_rbWindows);
        page.Controls.Add(_rbSql);
        var sqlAuthRow = MakeRow(
            new Label { Text = "User:", AutoSize = true, Margin = new Padding(20, 6, 4, 0) }, _txtSqlUser,
            new Label { Text = "Password:", AutoSize = true, Margin = new Padding(12, 6, 4, 0) }, _txtSqlPassword);
        page.Controls.Add(sqlAuthRow);

        page.Controls.Add(MakeFieldLabel("Database"));
        page.Controls.Add(MakeRow(_cboDatabase, _btnListDatabases));
        page.Controls.Add(MakeHint("Type the database name, or click \"List databases\" to pick from what's on the server."));

        page.Controls.Add(MakeRow(_btnTest));
        page.Controls.Add(_lblResult);

        Controls.Add(page);

        _rbWindows.CheckedChanged += (_, _) => UpdateAuthFields();
        _rbSql.CheckedChanged += (_, _) => UpdateAuthFields();
        _btnListDatabases.Click += async (_, _) => await ListDatabasesAsync();
        _btnTest.Click += async (_, _) => await TestConnectionAsync();

        _cboServer.Items.AddRange(SqlInstanceDiscovery.GetLocalInstances().Cast<object>().ToArray());
        _ = LoadNetworkInstancesAsync();
    }

    private async Task LoadNetworkInstancesAsync()
    {
        var found = await SqlInstanceDiscovery.DiscoverNetworkInstancesAsync();
        if (IsDisposed) return;
        BeginInvoke(new MethodInvoker(() =>
        {
            foreach (string name in found)
                if (!_cboServer.Items.Contains(name)) _cboServer.Items.Add(name);
        }));
    }

    private void UpdateAuthFields()
    {
        _txtSqlUser.Enabled = _rbSql.Checked;
        _txtSqlPassword.Enabled = _rbSql.Checked;
    }

    private InstallContext SnapshotFromFields(InstallContext context)
    {
        context.SqlServerInstance = _cboServer.Text.Trim();
        context.DatabaseName = _cboDatabase.Text.Trim();
        context.UseSqlAuth = _rbSql.Checked;
        context.SqlUser = _txtSqlUser.Text.Trim();
        context.SqlPassword = _txtSqlPassword.Text;
        return context;
    }

    private async Task ListDatabasesAsync()
    {
        if (string.IsNullOrWhiteSpace(_cboServer.Text))
        {
            ShowResult(_lblResult, "Enter a SQL Server instance first.", Color.DarkOrange);
            return;
        }
        _btnListDatabases.Enabled = false;
        ShowResult(_lblResult, "Listing databases...", Color.DimGray);
        var tempContext = SnapshotFromFields(new InstallContext());
        var installer = new AttachDatabaseInstaller(_ => { });
        var names = await installer.ListDatabasesAsync(tempContext, CancellationToken.None);
        _btnListDatabases.Enabled = true;
        if (names.Count == 0)
        {
            ShowResult(_lblResult, "Couldn't list databases (check the server name/auth) - type the database name in directly.", Color.DarkOrange);
            return;
        }
        string current = _cboDatabase.Text;
        _cboDatabase.Items.Clear();
        _cboDatabase.Items.AddRange(names.Cast<object>().ToArray());
        _cboDatabase.Text = current;
        ShowResult(_lblResult, $"Found {names.Count} database(s).", Color.SeaGreen);
    }

    private async Task TestConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(_cboServer.Text) || string.IsNullOrWhiteSpace(_cboDatabase.Text))
        {
            ShowResult(_lblResult, "Enter a server and database first.", Color.DarkOrange);
            return;
        }
        _btnTest.Enabled = false;
        ShowResult(_lblResult, "Connecting...", Color.DimGray);
        var tempContext = SnapshotFromFields(new InstallContext());
        var installer = new AttachDatabaseInstaller(_ => { });
        var (ok, error) = await installer.TestConnectionAsync(tempContext, CancellationToken.None);
        _btnTest.Enabled = true;
        _connectionTestedOk = ok;
        ShowResult(_lblResult, ok ? "Connected successfully." : $"Failed: {error}", ok ? Color.SeaGreen : Color.Firebrick);
    }

    private bool _connectionTestedOk;

    public override void OnEnter(InstallContext context)
    {
        _cboServer.Text = context.SqlServerInstance;
        _cboDatabase.Text = context.DatabaseName;
        _rbSql.Checked = context.UseSqlAuth;
        _rbWindows.Checked = !context.UseSqlAuth;
        _txtSqlUser.Text = context.SqlUser;
        _txtSqlPassword.Text = context.SqlPassword;
        UpdateAuthFields();
        _connectionTestedOk = context.ConnectionTested;
        if (_connectionTestedOk) ShowResult(_lblResult, "Connected successfully.", Color.SeaGreen);
    }

    public override void OnLeave(InstallContext context)
    {
        SnapshotFromFields(context);
        context.ConnectionTested = _connectionTestedOk;
    }

    public override bool ValidateStep(InstallContext context, out string error)
    {
        if (string.IsNullOrWhiteSpace(_cboServer.Text)) { error = "Enter a SQL Server instance."; return false; }
        if (string.IsNullOrWhiteSpace(_cboDatabase.Text)) { error = "Enter a database name."; return false; }
        if (_rbSql.Checked && string.IsNullOrWhiteSpace(_txtSqlUser.Text)) { error = "Enter a SQL login username."; return false; }
        if (!_connectionTestedOk)
        {
            var confirm = MessageBox.Show(this,
                "You haven't tested this connection yet (or the last test failed). Continue anyway?",
                "Connection not confirmed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) { error = "Click Test Connection first, or confirm you want to continue anyway."; return false; }
        }
        error = string.Empty;
        return true;
    }
}
