using GraniteNiFiDeploy.Core;
using GraniteNiFiDeploy.Models;

namespace GraniteNiFiDeploy.UI;

public sealed class Step3DatabaseControl : WizardStepControl
{
    public override string StepTitle => "Step 3 of 4: Granite database and feeds";

    private readonly ComboBox _cboServer = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly RadioButton _rbWindows = new() { Text = "Windows authentication (this account)", AutoSize = true, Checked = true };
    private readonly RadioButton _rbSql = new() { Text = "SQL login", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly TextBox _txtSqlUser = new() { Width = 200, Enabled = false };
    private readonly TextBox _txtSqlPassword = new() { Width = 200, Enabled = false, UseSystemPasswordChar = true };
    private readonly ComboBox _cboDatabase = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _btnListDatabases = MakeButton("List databases");
    private readonly Button _btnTest = MakeButton("Test connection", 130);
    private readonly Label _lblResult = new() { AutoSize = true, MaximumSize = new Size(680, 0), Margin = new Padding(0, 8, 0, 0) };

    private readonly Dictionary<string, CheckBox> _feedBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _txtLogin = MakeTextBox(200);
    private readonly CheckBox _chkValidateCert = new() { Text = "Validate the SQL Server's certificate (only if it has one from a trusted CA)", AutoSize = true };

    private readonly GroupBox _grpOrders = new() { Text = "Order defaults (written into the order import procs)", AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 8, 0, 0) };
    private readonly ComboBox _soType = Combo(), _soStatus = Combo(), _soSite = Combo();
    private readonly ComboBox _poType = Combo(), _poStatus = Combo(), _poSite = Combo();
    private readonly Button _btnReadValues = MakeButton("Read values from Document", 180);

    private SqlServerCheck? _check;
    private string _checkedFor = string.Empty;

    public Step3DatabaseControl()
    {
        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));

        page.Controls.Add(MakeFieldLabel("SQL Server"));
        page.Controls.Add(_cboServer);
        page.Controls.Add(MakeHint("As you'd type it in SSMS: SERVER, SERVER\\INSTANCE or SERVER,PORT. NiFi will use the same address over TCP."));

        page.Controls.Add(MakeFieldLabel("Sign in to deploy with (needs to create logins and objects)"));
        page.Controls.Add(_rbWindows);
        page.Controls.Add(_rbSql);
        page.Controls.Add(MakeRow(
            new Label { Text = "User:", AutoSize = true, Margin = new Padding(20, 6, 4, 0) }, _txtSqlUser,
            new Label { Text = "Password:", AutoSize = true, Margin = new Padding(12, 6, 4, 0) }, _txtSqlPassword));

        page.Controls.Add(MakeFieldLabel("Granite database"));
        page.Controls.Add(MakeRow(_cboDatabase, _btnListDatabases));
        page.Controls.Add(MakeRow(_btnTest));
        page.Controls.Add(_lblResult);

        page.Controls.Add(MakeFieldLabel("Feeds to set up (one Inbound folder each)"));
        var feedPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0) };
        foreach (var feed in Feeds.All)
        {
            var box = new CheckBox { Text = $"{feed.Title}  -  {feed.Description}", AutoSize = true, Checked = true, Tag = feed.Name };
            box.CheckedChanged += (_, _) => UpdateOrderGroup();
            _feedBoxes[feed.Name] = box;
            feedPanel.Controls.Add(box);
        }
        page.Controls.Add(feedPanel);

        var orders = new TableLayoutPanel { ColumnCount = 4, AutoSize = true };
        orders.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 0);
        orders.Controls.Add(new Label { Text = "Document type", AutoSize = true }, 1, 0);
        orders.Controls.Add(new Label { Text = "Status", AutoSize = true }, 2, 0);
        orders.Controls.Add(new Label { Text = "Site", AutoSize = true }, 3, 0);
        orders.Controls.Add(new Label { Text = "Sales orders", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, 1);
        orders.Controls.Add(_soType, 1, 1); orders.Controls.Add(_soStatus, 2, 1); orders.Controls.Add(_soSite, 3, 1);
        orders.Controls.Add(new Label { Text = "Purchase orders", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, 0, 2);
        orders.Controls.Add(_poType, 1, 2); orders.Controls.Add(_poStatus, 2, 2); orders.Controls.Add(_poSite, 3, 2);
        var orderPage = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        orderPage.Controls.Add(orders);
        orderPage.Controls.Add(MakeRow(_btnReadValues));
        orderPage.Controls.Add(MakeHint("Use the values this client's documents already have. Wrong values import cleanly, but the orders never show in WebDesktop."));
        _grpOrders.Controls.Add(orderPage);
        page.Controls.Add(_grpOrders);

        page.Controls.Add(MakeFieldLabel("SQL login NiFi signs in with"));
        page.Controls.Add(_txtLogin);
        page.Controls.Add(MakeHint("Created (or its password reset) with a random 32-character password that only NiFi keeps, encrypted. It can only insert into the staging tables and run the import procs."));
        page.Controls.Add(_chkValidateCert);

        Controls.Add(page);

        _rbWindows.CheckedChanged += (_, _) => UpdateAuthFields();
        _rbSql.CheckedChanged += (_, _) => UpdateAuthFields();
        _btnListDatabases.Click += async (_, _) => await ListDatabasesAsync();
        _btnTest.Click += async (_, _) => await TestConnectionAsync();
        _btnReadValues.Click += async (_, _) => await ReadDocumentValuesAsync();

        _cboServer.Items.AddRange(SqlInstanceDiscovery.GetLocalInstances().Cast<object>().ToArray());
        _ = LoadNetworkInstancesAsync();
    }

    private static ComboBox Combo() => new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 30 };

    private async Task LoadNetworkInstancesAsync()
    {
        var found = await SqlInstanceDiscovery.DiscoverNetworkInstancesAsync();
        if (IsDisposed || !IsHandleCreated) return;
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

    private void UpdateOrderGroup() =>
        _grpOrders.Enabled = _feedBoxes["SalesOrder"].Checked || _feedBoxes["PurchaseOrder"].Checked;

    private string Fingerprint() => $"{_cboServer.Text.Trim()}|{_cboDatabase.Text.Trim()}|{_rbSql.Checked}|{_txtSqlUser.Text.Trim()}|{_txtSqlPassword.Text}";

    private string ConnectionString(string? database = null) => SqlConnectionStrings.Build(
        _cboServer.Text.Trim(), database ?? _cboDatabase.Text.Trim(), _rbSql.Checked, _txtSqlUser.Text.Trim(), _txtSqlPassword.Text);

    private async Task ListDatabasesAsync()
    {
        if (string.IsNullOrWhiteSpace(_cboServer.Text)) { ShowResult(_lblResult, "Enter the SQL Server first.", Color.DarkOrange); return; }
        _btnListDatabases.Enabled = false;
        ShowResult(_lblResult, "Listing databases...", Color.DimGray);
        var names = await new SqlDeployer(_ => { }).ListDatabasesAsync(ConnectionString("master"), CancellationToken.None);
        _btnListDatabases.Enabled = true;
        if (names.Count == 0) { ShowResult(_lblResult, "Couldn't list databases (check the server and sign-in). You can type the name.", Color.DarkOrange); return; }
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
            ShowResult(_lblResult, "Enter the server and database first.", Color.DarkOrange);
            return;
        }
        try { JdbcUrl.Build(_cboServer.Text, _cboDatabase.Text, false); }
        catch (ArgumentException ex) { ShowResult(_lblResult, ex.Message, Color.Firebrick); return; }

        _btnTest.Enabled = false;
        ShowResult(_lblResult, "Connecting...", Color.DimGray);
        string fingerprint = Fingerprint();
        var (check, error) = await new SqlDeployer(_ => { }).CheckAsync(ConnectionString(), CancellationToken.None);
        _btnTest.Enabled = true;
        _check = check;
        _checkedFor = check is null ? string.Empty : fingerprint;

        if (check is null) { ShowResult(_lblResult, "Failed: " + error, Color.Firebrick); return; }
        var lines = new List<string> { $"Connected. SQL Server {check.Version}." };
        lines.AddRange(check.Blockers.Select(b => "STOP: " + b));
        if (check.FrameworkAlreadyDeployed) lines.Add("The NiFi import framework is already in this database: it will be updated in place (log history kept).");
        if (check.Blockers.Count == 0) lines.Add("Ready: SQL logins allowed, this sign-in can create the login and the objects, Granite tables found.");
        ShowResult(_lblResult, string.Join("\n", lines), check.Blockers.Count == 0 ? Color.SeaGreen : Color.Firebrick);

        if (check.Blockers.Count == 0) await ReadDocumentValuesAsync(quiet: true);
    }

    private async Task ReadDocumentValuesAsync(bool quiet = false)
    {
        try
        {
            var (types, statuses, sites) = await new SqlDeployer(_ => { }).DocumentValuesAsync(ConnectionString(), CancellationToken.None);
            Fill(types, _soType, _poType);
            Fill(statuses, _soStatus, _poStatus);
            Fill(sites, _soSite, _poSite);
            if (!quiet)
                MessageBox.Show(this, types.Count == 0 ? "No documents yet, so there's nothing to copy. Check the values with the Granite configuration." : "The dropdowns now list the values already used on documents.",
                    "Document values", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (!quiet)
        {
            MessageBox.Show(this, "Couldn't read dbo.Document: " + ex.Message, "Document values", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            // Quiet read after Test connection: the defaults stay as they are.
        }
    }

    private static void Fill(IReadOnlyList<string> values, params ComboBox[] boxes)
    {
        foreach (var box in boxes)
        {
            string current = box.Text;
            box.Items.Clear();
            box.Items.AddRange(values.Cast<object>().ToArray());
            box.Text = current;
        }
    }

    public override void OnEnter(DeployContext context)
    {
        if (_cboServer.Text.Length == 0) _cboServer.Text = context.SqlServerInstance;
        if (_cboDatabase.Text.Length == 0) _cboDatabase.Text = context.DatabaseName;
        _rbSql.Checked = context.UseSqlAuth;
        _rbWindows.Checked = !context.UseSqlAuth;
        _txtSqlUser.Text = context.SqlUser;
        _txtSqlPassword.Text = context.SqlPassword;
        _txtLogin.Text = context.NiFiSqlLogin;
        _chkValidateCert.Checked = context.ValidateSqlCertificate;
        foreach (var (name, box) in _feedBoxes) box.Checked = context.Feeds.Contains(name);
        SetOrder(context.SalesOrderDefaults, _soType, _soStatus, _soSite);
        SetOrder(context.PurchaseOrderDefaults, _poType, _poStatus, _poSite);
        UpdateAuthFields();
        UpdateOrderGroup();
    }

    private static void SetOrder(OrderDefaults d, ComboBox type, ComboBox status, ComboBox site)
    {
        type.Text = d.DocumentType;
        status.Text = d.DocumentStatus;
        site.Text = d.Site;
    }

    public override void OnLeave(DeployContext context)
    {
        context.SqlServerInstance = _cboServer.Text.Trim();
        context.DatabaseName = _cboDatabase.Text.Trim();
        context.UseSqlAuth = _rbSql.Checked;
        context.SqlUser = _txtSqlUser.Text.Trim();
        context.SqlPassword = _txtSqlPassword.Text;
        context.ConnectionTested = _check is not null && _checkedFor == Fingerprint();
        context.SqlCheck = context.ConnectionTested ? _check : null;
        context.NiFiSqlLogin = _txtLogin.Text.Trim();
        context.ValidateSqlCertificate = _chkValidateCert.Checked;
        context.Feeds.Clear();
        foreach (var (name, box) in _feedBoxes) if (box.Checked) context.Feeds.Add(name);
        context.SalesOrderDefaults = new OrderDefaults(_soType.Text.Trim(), _soStatus.Text.Trim(), _soSite.Text.Trim(), _soType.Text.Trim());
        context.PurchaseOrderDefaults = new OrderDefaults(_poType.Text.Trim(), _poStatus.Text.Trim(), _poSite.Text.Trim(), _poType.Text.Trim());
    }

    public override bool ValidateStep(DeployContext context, out string error)
    {
        if (string.IsNullOrWhiteSpace(_cboServer.Text)) { error = "Enter the SQL Server."; return false; }
        if (string.IsNullOrWhiteSpace(_cboDatabase.Text)) { error = "Enter the Granite database."; return false; }
        if (_rbSql.Checked && string.IsNullOrWhiteSpace(_txtSqlUser.Text)) { error = "Enter the SQL login to deploy with."; return false; }
        if (_check is null || _checkedFor != Fingerprint()) { error = "Click Test connection. The install needs to know this server and database are ready."; return false; }
        if (_check.Blockers.Count > 0) { error = string.Join("\n\n", _check.Blockers); return false; }
        if (!_feedBoxes.Values.Any(b => b.Checked)) { error = "Choose at least one feed."; return false; }
        if (!InputRules.IsValidSqlLogin(_txtLogin.Text.Trim(), out error)) return false;

        if (_feedBoxes["SalesOrder"].Checked &&
            !new OrderDefaults(_soType.Text.Trim(), _soStatus.Text.Trim(), _soSite.Text.Trim(), _soType.Text.Trim()).IsValid(out error)) { error = "Sales orders: " + error; return false; }
        if (_feedBoxes["PurchaseOrder"].Checked &&
            !new OrderDefaults(_poType.Text.Trim(), _poStatus.Text.Trim(), _poSite.Text.Trim(), _poType.Text.Trim()).IsValid(out error)) { error = "Purchase orders: " + error; return false; }

        error = string.Empty;
        return true;
    }
}
