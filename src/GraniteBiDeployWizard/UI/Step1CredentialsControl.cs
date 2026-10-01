using GraniteBiDeployWizard.Core;
using GraniteBiDeployWizard.Models;

namespace GraniteBiDeployWizard.UI;

public sealed class Step1CredentialsControl : WizardStepControl
{
    private readonly ComboBox _txtServer = new()
    {
        Width = 300,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems
    };
    private readonly Button _btnRefreshServers = new() { Text = "Refresh", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(90, 0), Margin = new Padding(6, 0, 0, 0) };

    // Was a plain TextBox -- now an editable dropdown, offered as a
    // convenience once a server has been picked. Stays freely typeable
    // (DropDownStyle.DropDown, not DropDownList) because the discovery
    // behind this is best-effort and often finds nothing: a login with no
    // visibility into other logins, a server the current Windows identity
    // can't reach at all, or simply a login that doesn't exist yet (the
    // "create one" bootstrap flow below is exactly for that case). See
    // SqlLoginDiscovery for why and how.
    private readonly ComboBox _txtUsername = new()
    {
        Width = 360,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems
    };
    private readonly TextBox _txtPassword = new() { Width = 360, UseSystemPasswordChar = true };
    private readonly Button _btnTest = new() { Text = "Test Connection", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(150, 0), Margin = new Padding(0, 12, 0, 0) };
    private readonly Label _lblResult = new() { AutoSize = true, Margin = new Padding(12, 16, 0, 0) };

    private readonly LinkLabel _lnkToggleBootstrap = new()
    {
        Text = "I don't have a login with database-creation rights -- create one",
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 4)
    };

    // ----- Bootstrap panel (create a dedicated SQL login) -------------------
    private readonly GroupBox _grpBootstrap = new()
    {
        Text = "Create a dedicated SQL login for this deployment",
        Width = 560,
        AutoSize = true,
        Visible = false,
        Padding = new Padding(12, 8, 12, 12),
        Margin = new Padding(0, 4, 0, 12)
    };

    private readonly RadioButton _rbBootstrapWindows = new()
    {
        Text = "Use Windows Authentication (the account this wizard is currently running as)",
        AutoSize = true,
        Checked = true
    };
    private readonly RadioButton _rbBootstrapSql = new() { Text = "Use a different SQL login", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly TextBox _txtBootstrapUsername = new() { Width = 300, Enabled = false };
    private readonly TextBox _txtBootstrapPassword = new() { Width = 300, UseSystemPasswordChar = true, Enabled = false };

    private readonly TextBox _txtSourceDb = new() { Width = 300 };
    private readonly TextBox _txtNewLoginName = new() { Width = 300, Text = "Granite_BI_User" };
    private readonly TextBox _txtNewLoginPassword = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly Button _btnGeneratePassword = new() { Text = "Generate", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(90, 0), Margin = new Padding(6, 0, 0, 0) };
    private readonly CheckBox _chkShowNewPassword = new() { Text = "Show", AutoSize = true, Margin = new Padding(6, 4, 0, 0) };
    private readonly Button _btnCreateLogin = new() { Text = "Create Login && Continue", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(190, 0), Margin = new Padding(0, 12, 0, 0) };
    private readonly Label _lblBootstrapResult = new() { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 8, 0, 0) };

    public override string StepTitle => "Step 1 of 6: SQL Server Connection";

    public Step1CredentialsControl()
    {
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true
        };

        layout.Controls.Add(MakeHeading(StepTitle));
        layout.Controls.Add(MakeFieldLabel("SQL Server / instance name:"));
        var serverRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        serverRow.Controls.Add(_txtServer);
        serverRow.Controls.Add(_btnRefreshServers);
        layout.Controls.Add(serverRow);
        layout.Controls.Add(MakeHint(
            "Pick a detected instance, or type one -- e.g. SQL01\\GRANITE or 192.168.1.10,1433. " +
            "The list only includes what's installed on this machine or answers on the network " +
            "(both best-effort -- a firewalled or Browser-service-off instance won't show up here " +
            "even though it's perfectly reachable by name)."));

        layout.Controls.Add(MakeFieldLabel("SQL username:"));
        layout.Controls.Add(_txtUsername);
        layout.Controls.Add(MakeHint(
            "If the account this wizard is running as can see other SQL logins on the server picked " +
            "above, they're offered here as suggestions once you pick or leave that field -- otherwise " +
            "this just stays a normal box to type the login name into."));

        layout.Controls.Add(MakeFieldLabel("Password:"));
        layout.Controls.Add(_txtPassword);
        layout.Controls.Add(MakeHint(
            "Every connection this wizard opens to actually deploy or query the BI data is a " +
            "fresh, unpooled session built from exactly these credentials (Windows authentication " +
            "is never used as a fallback here), so nothing here can be silently satisfied by a " +
            "cached connection instead of the SQL login entered above."));

        var testRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        testRow.Controls.Add(_btnTest);
        testRow.Controls.Add(_lblResult);
        layout.Controls.Add(testRow);

        layout.Controls.Add(_lnkToggleBootstrap);
        BuildBootstrapPanel();
        layout.Controls.Add(_grpBootstrap);

        Controls.Add(layout);

        PopulateServerList();
        _btnRefreshServers.Click += async (_, _) => await RefreshServerListAsync();
        // Triggered on the two natural "the server field is settled" points
        // -- picking an item from the list, or tabbing/clicking away after
        // typing one -- rather than on every keystroke, so this isn't
        // opening a connection attempt per character typed.
        _txtServer.SelectedIndexChanged += async (_, _) => await RefreshUsernameSuggestionsAsync();
        _txtServer.Leave += async (_, _) => await RefreshUsernameSuggestionsAsync();
        _btnTest.Click += async (_, _) => await TestConnectionAsync();
        _lnkToggleBootstrap.Click += (_, _) => SetBootstrapVisible(!_grpBootstrap.Visible);
        _rbBootstrapWindows.CheckedChanged += (_, _) => UpdateBootstrapAuthFieldsEnabled();
        _rbBootstrapSql.CheckedChanged += (_, _) => UpdateBootstrapAuthFieldsEnabled();
        _btnGeneratePassword.Click += (_, _) =>
        {
            _txtNewLoginPassword.Text = BootstrapLoginService.GenerateStrongPassword();
            _chkShowNewPassword.Checked = true;
        };
        _chkShowNewPassword.CheckedChanged += (_, _) =>
            _txtNewLoginPassword.UseSystemPasswordChar = !_chkShowNewPassword.Checked;
        _btnCreateLogin.Click += async (_, _) => await CreateDedicatedLoginAsync();
    }

    private void BuildBootstrapPanel()
    {
        var inner = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false
        };

        inner.Controls.Add(MakeHint(
            "If the login above can connect but not create databases (or you simply don't have " +
            "credentials with that kind of access to this server), use an admin account you do " +
            "have -- your own Windows login on this machine is usually enough on a freshly " +
            "installed SQL Server Express instance -- to create a dedicated, least-privilege SQL " +
            "login just for this deployment. That admin account is used once, right now, and is " +
            "never stored or used again."));

        inner.Controls.Add(_rbBootstrapWindows);
        inner.Controls.Add(_rbBootstrapSql);

        inner.Controls.Add(MakeFieldLabel("Admin SQL username:"));
        inner.Controls.Add(_txtBootstrapUsername);
        inner.Controls.Add(MakeFieldLabel("Admin password:"));
        inner.Controls.Add(_txtBootstrapPassword);
        inner.Controls.Add(MakeHint("That admin account needs the sysadmin or securityadmin server role -- either is enough to create a login and grant it dbcreator."));

        inner.Controls.Add(MakeFieldLabel("New login name:"));
        inner.Controls.Add(_txtNewLoginName);
        inner.Controls.Add(MakeHint("Letters, digits, and underscores only. Reused safely if this wizard is run again against the same server."));

        inner.Controls.Add(MakeFieldLabel("Source database (the live GraniteWMS database this login needs to read from):"));
        inner.Controls.Add(_txtSourceDb);
        inner.Controls.Add(MakeHint(
            "dbcreator only covers creating the new BI database -- it says nothing about the " +
            "existing source database the scripts read from via three-part names like " +
            "GraniteLive.dbo.vw_BI_.... Without read access there too, deployment will get all the " +
            "way through CREATE DATABASE and then fail on \"Invalid object name\" for every view/table " +
            "that reads from the source. This needs the admin account above to be sysadmin (or " +
            "already db_owner of this specific database) -- securityadmin alone can create the login " +
            "but can't grant rights inside an existing database. Step 2 checks the source database's " +
            "own reporting views separately -- this field is just for the read-access grant here."));

        inner.Controls.Add(MakeFieldLabel("New login password:"));
        var pwdRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        pwdRow.Controls.Add(_txtNewLoginPassword);
        pwdRow.Controls.Add(_btnGeneratePassword);
        pwdRow.Controls.Add(_chkShowNewPassword);
        inner.Controls.Add(pwdRow);
        inner.Controls.Add(MakeHint("Write this down -- it's shown here once and is not saved anywhere by this wizard."));

        inner.Controls.Add(_btnCreateLogin);
        inner.Controls.Add(_lblBootstrapResult);

        _grpBootstrap.Controls.Add(inner);
    }

    /// <summary>Fast, synchronous: just reads the local registry key SQL Server setup maintains.</summary>
    private void PopulateServerList()
    {
        foreach (string name in SqlInstanceDiscovery.GetLocalInstances())
        {
            if (!_txtServer.Items.Contains(name))
                _txtServer.Items.Add(name);
        }

        // Kick off the slower, network-broadcast source without blocking
        // construction -- it fills in a few seconds later, by which point
        // the user is very likely still filling in other fields on Panel 1.
        _ = RefreshServerListAsync(includeLocal: false);
    }

    private async Task RefreshServerListAsync(bool includeLocal = true)
    {
        _btnRefreshServers.Enabled = false;
        try
        {
            if (includeLocal)
            {
                foreach (string name in SqlInstanceDiscovery.GetLocalInstances())
                {
                    if (!_txtServer.Items.Contains(name))
                        _txtServer.Items.Add(name);
                }
            }

            foreach (string name in await SqlInstanceDiscovery.DiscoverNetworkInstancesAsync())
            {
                if (!_txtServer.Items.Contains(name))
                    _txtServer.Items.Add(name);
            }
        }
        finally
        {
            _btnRefreshServers.Enabled = true;
        }
    }

    // Avoids re-querying the same server over and over as SelectedIndexChanged
    // and Leave both fire, or as OnEnter re-runs this on every Back/Next
    // through Panel 1 -- see RefreshUsernameSuggestionsAsync.
    private string? _lastLoginDiscoveryServer;

    /// <summary>
    /// Best-effort: offers other SQL logins on the picked server as
    /// suggestions in the username dropdown. See <see cref="SqlLoginDiscovery"/>
    /// for why this can come back empty (most of the time, for most users)
    /// without that being treated as an error anywhere.
    /// </summary>
    private async Task RefreshUsernameSuggestionsAsync()
    {
        string server = _txtServer.Text.Trim();
        if (string.IsNullOrWhiteSpace(server) || string.Equals(server, _lastLoginDiscoveryServer, StringComparison.OrdinalIgnoreCase))
            return;
        _lastLoginDiscoveryServer = server;

        var names = await SqlLoginDiscovery.GetSqlLoginNamesAsync(server);
        if (names.Count == 0)
            return;

        // The server field may have changed again while this was in
        // flight -- only apply results that are still for what's actually
        // in the box now.
        if (!string.Equals(_txtServer.Text.Trim(), server, StringComparison.OrdinalIgnoreCase))
            return;

        string currentText = _txtUsername.Text;
        _txtUsername.Items.Clear();
        foreach (string name in names)
            _txtUsername.Items.Add(name);
        _txtUsername.Text = currentText; // whatever the user already typed/OnEnter set -- never overwritten by suggestions arriving
    }

    private void SetBootstrapVisible(bool visible)
    {
        _grpBootstrap.Visible = visible;
        _lnkToggleBootstrap.Text = visible
            ? "Hide the dedicated-login option"
            : "I don't have a login with database-creation rights -- create one";
    }

    private void UpdateBootstrapAuthFieldsEnabled()
    {
        bool useSql = _rbBootstrapSql.Checked;
        _txtBootstrapUsername.Enabled = useSql;
        _txtBootstrapPassword.Enabled = useSql;
    }

    private async Task TestConnectionAsync()
    {
        _btnTest.Enabled = false;
        _lblResult.ForeColor = Color.DimGray;
        _lblResult.Text = "Testing...";

        string server = _txtServer.Text.Trim();
        string username = _txtUsername.Text.Trim();
        string password = _txtPassword.Text;

        var (success, message) = await SqlConnectionFactory.TestConnectionAsync(server, username, password);

        if (!success)
        {
            _lblResult.ForeColor = Color.Firebrick;
            _lblResult.Text = message;
            _btnTest.Enabled = true;
            return;
        }

        string connectionString = SqlConnectionFactory.BuildConnectionString(server, "master", username, password);
        var roles = await BootstrapLoginService.CheckRolesAsync(connectionString);

        if (!roles.Success)
        {
            // Odd (same credentials just connected above), but not fatal --
            // fall back to the base success message and let the user open
            // the bootstrap panel manually if they already know they need it.
            _lblResult.ForeColor = Color.SeaGreen;
            _lblResult.Text = message;
        }
        else if (roles.CanCreateDatabases)
        {
            _lblResult.ForeColor = Color.SeaGreen;
            _lblResult.Text = $"{message} This login can create the BI database.";
            SetBootstrapVisible(false);
        }
        else
        {
            _lblResult.ForeColor = Color.DarkOrange;
            _lblResult.Text = $"{message} This login cannot create databases (no dbcreator/sysadmin role).";
            SetBootstrapVisible(true);
        }

        _btnTest.Enabled = true;
    }

    private async Task CreateDedicatedLoginAsync()
    {
        string server = _txtServer.Text.Trim();
        string newLoginName = _txtNewLoginName.Text.Trim();
        string newLoginPassword = _txtNewLoginPassword.Text;
        string sourceDb = _txtSourceDb.Text.Trim();

        if (string.IsNullOrWhiteSpace(server))
        {
            ShowBootstrapError("Enter the SQL Server / instance name above first.");
            return;
        }
        if (!BootstrapLoginService.IsValidLoginName(newLoginName))
        {
            ShowBootstrapError("Login name must start with a letter or underscore and contain only letters, digits, and underscores.");
            return;
        }
        if (string.IsNullOrWhiteSpace(newLoginPassword) || newLoginPassword.Length < 8)
        {
            ShowBootstrapError("Enter (or generate) a password of at least 8 characters for the new login.");
            return;
        }
        if (string.IsNullOrWhiteSpace(sourceDb))
        {
            ShowBootstrapError("Enter the source database name (e.g. GraniteLive) so read access can be granted there too.");
            return;
        }
        if (_rbBootstrapSql.Checked && string.IsNullOrWhiteSpace(_txtBootstrapUsername.Text))
        {
            ShowBootstrapError("Enter the admin SQL username, or switch to Windows Authentication above.");
            return;
        }

        _btnCreateLogin.Enabled = false;
        _lblBootstrapResult.ForeColor = Color.DimGray;
        _lblBootstrapResult.Text = "Connecting with the admin credentials...";

        string adminConnectionString = _rbBootstrapWindows.Checked
            ? SqlConnectionFactory.BuildIntegratedConnectionString(server, "master")
            : SqlConnectionFactory.BuildConnectionString(server, "master", _txtBootstrapUsername.Text.Trim(), _txtBootstrapPassword.Text);

        var adminRoles = await BootstrapLoginService.CheckRolesAsync(adminConnectionString);
        if (!adminRoles.Success)
        {
            ShowBootstrapError($"Could not connect with the admin credentials: {adminRoles.Message}");
            _btnCreateLogin.Enabled = true;
            return;
        }
        if (!adminRoles.CanManageLogins)
        {
            ShowBootstrapError(
                "That admin account can't create SQL logins either -- it needs the sysadmin or " +
                "securityadmin server role. Try a different Windows admin account, or ask whoever " +
                "manages this SQL Server to run the grant for you.");
            _btnCreateLogin.Enabled = true;
            return;
        }

        try
        {
            await BootstrapLoginService.CreateOrRepairDeploymentLoginAsync(adminConnectionString, newLoginName, newLoginPassword);
        }
        catch (Exception ex)
        {
            ShowBootstrapError($"Could not create the login: {ex.Message}");
            _btnCreateLogin.Enabled = true;
            return;
        }

        // Confirm the new login actually authenticates and has the rights
        // it needs before handing it to the rest of the wizard.
        string newLoginConnectionString = SqlConnectionFactory.BuildConnectionString(server, "master", newLoginName, newLoginPassword);
        var confirmRoles = await BootstrapLoginService.CheckRolesAsync(newLoginConnectionString);

        if (!confirmRoles.Success || !confirmRoles.CanCreateDatabases)
        {
            string why = confirmRoles.Success ? "the dbcreator grant did not take effect" : confirmRoles.Message;
            ShowBootstrapError($"The login was created, but the wizard could not confirm it can create databases ({why}). You may need to grant dbcreator manually.");
            _btnCreateLogin.Enabled = true;
            return;
        }

        // Second, separate grant: read access inside the existing source
        // database. This can legitimately fail even when dbcreator above
        // succeeded (a securityadmin-only admin account can create logins
        // but has no rights inside an arbitrary existing database) -- treat
        // it as its own outcome rather than folding it into the result above,
        // so the user knows exactly which half needs attention if one fails.
        bool sourceDbGranted;
        string sourceDbStatus;
        try
        {
            await BootstrapLoginService.GrantSourceDatabaseReadAccessAsync(adminConnectionString, newLoginName, sourceDb);
            sourceDbGranted = true;
            sourceDbStatus = $"Granted read access on [{sourceDb}].";
        }
        catch (Exception ex)
        {
            sourceDbGranted = false;
            sourceDbStatus =
                $"Could NOT grant read access on [{sourceDb}] ({ex.Message.TrimEnd('.', ' ')}). Deployment will " +
                $"fail with \"Invalid object name\" on anything reading from {sourceDb} until this is fixed -- " +
                "ask your DBA to run: ALTER ROLE db_datareader ADD MEMBER [" + newLoginName + "]; in " +
                $"{sourceDb}, or redo this step with a sysadmin admin account.";
        }

        // Success: this dedicated login now becomes the credential the rest
        // of the wizard (and the deployment run) uses -- exactly as if the
        // user had typed it into the fields above themselves.
        _txtUsername.Text = newLoginName;
        _txtPassword.Text = newLoginPassword;
        _createdDedicatedLoginThisSession = true;

        _lblResult.ForeColor = Color.SeaGreen;
        _lblResult.Text = $"Using dedicated login \"{newLoginName}\" (created just now, has dbcreator).";

        _lblBootstrapResult.ForeColor = sourceDbGranted ? Color.SeaGreen : Color.DarkOrange;
        _lblBootstrapResult.Text = $"Login \"{newLoginName}\" created/updated and granted dbcreator. {sourceDbStatus}";
        _btnCreateLogin.Enabled = true;
        SetBootstrapVisible(!sourceDbGranted);
    }

    private void ShowBootstrapError(string message)
    {
        _lblBootstrapResult.ForeColor = Color.Firebrick;
        _lblBootstrapResult.Text = message;
    }

    private bool _createdDedicatedLoginThisSession;

    public override void OnEnter(DeploymentContext context)
    {
        _txtServer.Text = context.Server;
        _txtUsername.Text = context.SqlUsername;
        _txtPassword.Text = context.SqlPassword;
        // Panel 3 (Database Names) hasn't necessarily been visited yet on a
        // fresh run, but SourceDb already carries a sensible default -- and
        // if the user has been here before (Back from a later panel), this
        // picks up whatever they actually set there.
        _txtSourceDb.Text = context.SourceDb;
        _lblResult.Text = string.Empty;
        _lblBootstrapResult.Text = string.Empty;
        SetBootstrapVisible(false);

        // Covers arriving here with a server already set (Back from a later
        // panel) -- SelectedIndexChanged/Leave above only fire on user
        // interaction with the field, not on this programmatic Text set.
        // Fire-and-forget, same as PopulateServerList's own network refresh:
        // best-effort, never blocks the step from displaying.
        _ = RefreshUsernameSuggestionsAsync();
    }

    public override void OnLeave(DeploymentContext context)
    {
        context.Server = _txtServer.Text.Trim();
        context.SqlUsername = _txtUsername.Text.Trim();
        context.SqlPassword = _txtPassword.Text;
        context.UsedDedicatedBootstrapLogin = context.UsedDedicatedBootstrapLogin || _createdDedicatedLoginThisSession;
    }

    public override bool ValidateStep(DeploymentContext context, out string error)
    {
        if (string.IsNullOrWhiteSpace(_txtServer.Text)) { error = "Enter the SQL Server / instance name."; return false; }
        if (string.IsNullOrWhiteSpace(_txtUsername.Text)) { error = "Enter the SQL username."; return false; }
        if (string.IsNullOrWhiteSpace(_txtPassword.Text)) { error = "Enter the password."; return false; }

        error = string.Empty;
        return true;
    }
}
