using GraniteBiDeployWizard.Core;
using GraniteBiDeployWizard.Models;

namespace GraniteBiDeployWizard.UI;

public sealed class Step4ScheduleControl : WizardStepControl
{
    // ----- Scheduler mechanism choice ---------------------------------------
    private readonly RadioButton _rbSchedulerTask = new()
    {
        Text = "Windows Task Scheduler (default) -- works on Express and Standard/Enterprise",
        AutoSize = true,
        Checked = true
    };
    private readonly RadioButton _rbSchedulerAgent = new()
    {
        Text = "SQL Server Agent -- Standard/Enterprise only, gives job history and alerting in SSMS",
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 0)
    };
    private readonly RadioButton _rbSchedulerGranite = new()
    {
        Text = "Granite Scheduler -- works on Express and Standard/Enterprise, via Granite's own Scheduler service",
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 0)
    };
    private readonly RadioButton _rbSchedulerSkip = new()
    {
        Text = "Skip for now / set up scheduling manually -- nothing is registered automatically",
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 0)
    };
    private readonly Label _lblEditionCheck = new()
    {
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 6, 0, 12)
    };

    private readonly ComboBox _cmbInterval = new()
    {
        Width = 200,
        DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly ComboBox _txtAccount = new()
    {
        Width = 300,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems
    };
    private readonly Button _btnRefreshAccounts = new() { Text = "Refresh", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(90, 0), Margin = new Padding(6, 0, 0, 0) };
    private readonly TextBox _txtAccountPassword = new() { Width = 360, UseSystemPasswordChar = true };
    private readonly CheckBox _chkShowAccountPassword = new() { Text = "Show", AutoSize = true, Margin = new Padding(6, 4, 0, 0) };
    private readonly Button _btnVerifyAccount = new() { Text = "Verify Account", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(150, 0), Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _lblVerifyResult = new() { AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(12, 12, 0, 0) };

    private readonly LinkLabel _lnkToggleBootstrap = new()
    {
        Text = "This account doesn't have a SQL login yet -- create one",
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 4)
    };

    // ----- Bootstrap panel (map the Windows account to a SQL login) --------
    private readonly GroupBox _grpBootstrap = new()
    {
        Text = "Map this Windows account to a SQL Server login",
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
    private readonly Button _btnCreateLogin = new() { Text = "Create/Verify SQL Login", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(190, 0), Margin = new Padding(0, 12, 0, 0) };
    private readonly Label _lblBootstrapResult = new() { AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(0, 8, 0, 0) };

    /// <summary>
    /// Everything below the interval picker that only applies to the
    /// Windows Task Scheduler path (the account it runs as, its password,
    /// Verify Account, and the SQL-login bootstrap panel) -- shown or
    /// hidden as one unit depending on which radio button above is checked.
    /// None of it is meaningful for a SQL Server Agent job, which runs as
    /// its owner (the Panel 1 SQL login) rather than a Windows account.
    /// </summary>
    private readonly FlowLayoutPanel _taskSchedulerSection = new()
    {
        FlowDirection = FlowDirection.TopDown,
        AutoSize = true,
        WrapContents = false,
        Margin = new Padding(0)
    };

    private readonly Label _lblAgentNote = new()
    {
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 4, 0, 12),
        Visible = false,
        Text = "The job step runs as its owner -- the SQL login from Step 1 -- so no Windows " +
               "account is needed here. That login already has every right it used to deploy " +
               "the rest of the BI database."
    };

    private readonly Label _lblGraniteNote = new()
    {
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 4, 0, 12),
        Visible = false,
        Text = "Creates a small proxy procedure (dbo.usp_RunGraniteBiSync) in your live " +
               "database that calls into the BI database, and registers it in Granite's own " +
               "dbo.ScheduledJobs table -- the same kind of footprint Step 2 already leaves " +
               "with the reporting views. EXECUTE on the BI sync is granted to the \"Granite\" " +
               "SQL login the Scheduler service normally connects as, falling back to the " +
               "public role only if that login isn't present on this instance. Newest of the " +
               "three real options here -- please confirm the job actually fires on a real " +
               "deployment."
    };

    private readonly Label _lblSkipNote = new()
    {
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
        ForeColor = Color.DimGray,
        Margin = new Padding(0, 4, 0, 12),
        Visible = false,
        Text = "Run_BI_Sync.bat is still generated in the script folder, ready to wire into " +
               "whatever you decide on later -- Task Scheduler, SQL Server Agent, or Granite " +
               "Scheduler. Nothing runs it automatically until then, so the BI tables will " +
               "fall behind after the initial sync."
    };

    private readonly Label _lblIntervalField;

    private DeploymentContext? _context;
    private bool _editionCheckInFlight;

    public override string StepTitle => "Step 5 of 6: Background Sync Schedule";

    public Step4ScheduleControl()
    {
        _cmbInterval.Items.AddRange(new object[] { "Every 5 minutes", "Every 15 minutes", "Every 30 minutes" });
        _cmbInterval.SelectedIndex = 1;
        _lblIntervalField = MakeFieldLabel("Sync interval:");

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true
        };

        layout.Controls.Add(MakeHeading(StepTitle));

        layout.Controls.Add(MakeFieldLabel("Run the recurring sync using:"));
        layout.Controls.Add(_rbSchedulerTask);
        layout.Controls.Add(_rbSchedulerAgent);
        layout.Controls.Add(_rbSchedulerGranite);
        layout.Controls.Add(_rbSchedulerSkip);
        layout.Controls.Add(_lblEditionCheck);

        layout.Controls.Add(_lblIntervalField);
        layout.Controls.Add(_cmbInterval);
        layout.Controls.Add(_lblAgentNote);
        layout.Controls.Add(_lblGraniteNote);
        layout.Controls.Add(_lblSkipNote);

        var accountRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        accountRow.Controls.Add(_txtAccount);
        accountRow.Controls.Add(_btnRefreshAccounts);

        var passwordRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        passwordRow.Controls.Add(_txtAccountPassword);
        passwordRow.Controls.Add(_chkShowAccountPassword);

        var verifyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        verifyRow.Controls.Add(_btnVerifyAccount);
        verifyRow.Controls.Add(_lblVerifyResult);

        _taskSchedulerSection.Controls.Add(MakeFieldLabel(@"Windows account to run the task as (e.g. .\svc-granitebi or DOMAIN\svc-granitebi):"));
        _taskSchedulerSection.Controls.Add(accountRow);
        _taskSchedulerSection.Controls.Add(MakeHint(
            "The dropdown lists this machine's local accounts only -- a domain account " +
            "(DOMAIN\\svc-account) can still be typed in directly, it just isn't looked up here."));

        _taskSchedulerSection.Controls.Add(MakeFieldLabel("Account password:"));
        _taskSchedulerSection.Controls.Add(passwordRow);
        _taskSchedulerSection.Controls.Add(MakeHint(
            "This account must itself have a login on the SQL Server instance, because the " +
            "generated Run_BI_Sync.bat calls sqlcmd with -E (trusted/integrated authentication) " +
            "-- no SQL password is ever written into the .bat file. The account and password " +
            "here are used only to register the scheduled task. A bare name like \"svc-bi\" is " +
            "treated as a local account (same as \".\\svc-bi\"); use DOMAIN\\user for a domain " +
            "account. Moving off this step verifies the account and password with Windows -- " +
            "the same check \"Verify Account\" below runs on demand -- so a typo is caught here, " +
            "not after the whole deployment has already run. If Windows rejects a password you're " +
            "sure is right, tick \"Show\" first and check for a stray space or autocomplete slip " +
            "before assuming the account itself is the problem -- the masked field hides exactly " +
            "that kind of typo."));

        _taskSchedulerSection.Controls.Add(verifyRow);
        _taskSchedulerSection.Controls.Add(_lnkToggleBootstrap);
        BuildBootstrapPanel();
        _taskSchedulerSection.Controls.Add(_grpBootstrap);

        layout.Controls.Add(_taskSchedulerSection);

        Controls.Add(layout);

        PopulateAccountList();
        _chkShowAccountPassword.CheckedChanged += (_, _) =>
            _txtAccountPassword.UseSystemPasswordChar = !_chkShowAccountPassword.Checked;
        _btnRefreshAccounts.Click += (_, _) => PopulateAccountList();
        _btnVerifyAccount.Click += async (_, _) => await VerifyAccountAsync();
        _lnkToggleBootstrap.Click += (_, _) => SetBootstrapVisible(!_grpBootstrap.Visible);
        _rbBootstrapWindows.CheckedChanged += (_, _) => UpdateBootstrapAuthFieldsEnabled();
        _rbBootstrapSql.CheckedChanged += (_, _) => UpdateBootstrapAuthFieldsEnabled();
        _btnCreateLogin.Click += async (_, _) => await CreateOrVerifyLoginAsync();
        _rbSchedulerTask.CheckedChanged += (_, _) => UpdateSchedulerSectionVisibility();
        _rbSchedulerAgent.CheckedChanged += (_, _) => UpdateSchedulerSectionVisibility();
        _rbSchedulerGranite.CheckedChanged += (_, _) => UpdateSchedulerSectionVisibility();
        _rbSchedulerSkip.CheckedChanged += (_, _) => UpdateSchedulerSectionVisibility();
        UpdateSchedulerSectionVisibility();
    }

    private void BuildBootstrapPanel()
    {
        var inner = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false
        };

        inner.Controls.Add(MakeHint(
            "A brand-new Windows account almost never has a SQL Server login yet. Use an admin " +
            "account you do have -- your own Windows login on this machine is usually enough on a " +
            "freshly installed SQL Server Express instance -- to create one now. This maps the " +
            "account above to a login (CREATE LOGIN ... FROM WINDOWS); the wizard grants it EXECUTE " +
            "on bi.usp_RunSync automatically at the end of Step 5, once the BI database exists. " +
            "That admin account is used once, right now, and is never stored or used again."));

        inner.Controls.Add(_rbBootstrapWindows);
        inner.Controls.Add(_rbBootstrapSql);

        inner.Controls.Add(MakeFieldLabel("Admin SQL username:"));
        inner.Controls.Add(_txtBootstrapUsername);
        inner.Controls.Add(MakeFieldLabel("Admin password:"));
        inner.Controls.Add(_txtBootstrapPassword);
        inner.Controls.Add(MakeHint("That admin account needs the sysadmin or securityadmin server role -- either is enough to create a login."));

        inner.Controls.Add(_btnCreateLogin);
        inner.Controls.Add(_lblBootstrapResult);

        _grpBootstrap.Controls.Add(inner);
    }

    /// <summary>
    /// Local-machine account lookup only (see WindowsAccountDiscovery's
    /// remarks on why domain accounts are deliberately excluded); fast
    /// enough to run synchronously on the UI thread, unlike Panel 1's
    /// network SQL instance discovery.
    /// </summary>
    private void PopulateAccountList()
    {
        _btnRefreshAccounts.Enabled = false;
        try
        {
            foreach (string name in WindowsAccountDiscovery.GetLocalUserAccounts())
            {
                if (!_txtAccount.Items.Contains(name))
                    _txtAccount.Items.Add(name);
            }
        }
        finally
        {
            _btnRefreshAccounts.Enabled = true;
        }
    }

    private void SetBootstrapVisible(bool visible)
    {
        _grpBootstrap.Visible = visible;
        _lnkToggleBootstrap.Text = visible
            ? "Hide the SQL login option"
            : "This account doesn't have a SQL login yet -- create one";
    }

    private void UpdateBootstrapAuthFieldsEnabled()
    {
        bool useSql = _rbBootstrapSql.Checked;
        _txtBootstrapUsername.Enabled = useSql;
        _txtBootstrapPassword.Enabled = useSql;
    }

    private async Task CreateOrVerifyLoginAsync()
    {
        if (_context is null) return;

        string server = _context.Server;
        string account = NormalizeAccountName(_txtAccount.Text.Trim());
        _txtAccount.Text = account;

        if (string.IsNullOrWhiteSpace(server))
        {
            ShowBootstrapError("Go back to Step 1 and enter the SQL Server / instance name first.");
            return;
        }
        if (!BootstrapLoginService.IsValidWindowsAccountName(account))
        {
            ShowBootstrapError(@"Enter the Windows account above first, in DOMAIN\user or .\user form.");
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
            await BootstrapLoginService.CreateOrVerifyWindowsLoginAsync(adminConnectionString, account);
        }
        catch (Exception ex)
        {
            ShowBootstrapError($"Could not create/verify the login: {ex.Message}");
            _btnCreateLogin.Enabled = true;
            return;
        }

        _lblBootstrapResult.ForeColor = Color.SeaGreen;
        _lblBootstrapResult.Text =
            $"SQL Server login for \"{account}\" is ready. It'll be granted rights to run the sync " +
            "automatically once the BI database is deployed on the next panel.";
        _btnCreateLogin.Enabled = true;
    }

    private void ShowBootstrapError(string message)
    {
        _lblBootstrapResult.ForeColor = Color.Firebrick;
        _lblBootstrapResult.Text = message;
    }

    /// <summary>
    /// A bare name with no domain qualifier (what actually got typed on the
    /// real run this feature was built for) is treated as a local account,
    /// same as ".\name" -- but is normalized to that explicit form here so
    /// every downstream consumer (Task Scheduler registration, and the
    /// CREATE LOGIN ... FROM WINDOWS check on the bootstrap panel below)
    /// sees the same unambiguous shape instead of three different guesses.
    /// UPN-form names (user@domain.com) are left alone.
    /// </summary>
    private static string NormalizeAccountName(string account)
    {
        if (string.IsNullOrWhiteSpace(account)) return account;
        return account.Contains('\\') || account.Contains('@') ? account : $@".\{account}";
    }

    private async Task VerifyAccountAsync()
    {
        string account = NormalizeAccountName(_txtAccount.Text.Trim());
        _txtAccount.Text = account;
        string password = _txtAccountPassword.Text;

        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(password))
        {
            _lblVerifyResult.ForeColor = Color.Firebrick;
            _lblVerifyResult.Text = "Enter the account and password first.";
            return;
        }

        _btnVerifyAccount.Enabled = false;
        _lblVerifyResult.ForeColor = Color.DimGray;
        _lblVerifyResult.Text = "Verifying with Windows...";

        var result = await Task.Run(() => WindowsCredentialValidator.Validate(account, password));

        _lblVerifyResult.ForeColor = result.Success ? Color.SeaGreen : Color.Firebrick;
        _lblVerifyResult.Text = result.Message;
        _btnVerifyAccount.Enabled = true;
    }

    private void UpdateSchedulerSectionVisibility()
    {
        _taskSchedulerSection.Visible = _rbSchedulerTask.Checked;
        _lblAgentNote.Visible = _rbSchedulerAgent.Checked;
        _lblGraniteNote.Visible = _rbSchedulerGranite.Checked;
        _lblSkipNote.Visible = _rbSchedulerSkip.Checked;

        // The interval only means anything to a mechanism that's actually
        // going to be registered -- hide it rather than leave a control
        // sitting there with no effect when skipping.
        bool intervalMatters = !_rbSchedulerSkip.Checked;
        _lblIntervalField.Visible = intervalMatters;
        _cmbInterval.Visible = intervalMatters;
    }

    /// <summary>
    /// Runs SERVERPROPERTY('EngineEdition') against the Step 1 server as
    /// soon as this step is shown, so the SQL Server Agent radio is already
    /// correctly enabled/disabled before the user looks at it rather than
    /// flipping under them a moment later. Best-effort: a failed check
    /// leaves the option enabled with a note, never wrongly disabled --
    /// see DeploymentContext.SqlEngineEdition.
    /// </summary>
    private async Task DetectEditionAsync(DeploymentContext context)
    {
        if (_editionCheckInFlight) return;
        _editionCheckInFlight = true;

        _lblEditionCheck.ForeColor = Color.DimGray;
        _lblEditionCheck.Text = "Checking whether this SQL Server instance has the Agent service...";

        var (success, edition, _) = await SqlConnectionFactory.DetectEngineEditionAsync(
            context.Server, context.SqlUsername, context.SqlPassword);

        context.SqlEngineEdition = success ? edition : null;
        ApplyEditionCheckResult();
        _editionCheckInFlight = false;
    }

    private void ApplyEditionCheckResult()
    {
        // EngineEdition 4 = Express, the only edition with no Agent service.
        bool isExpress = _context?.SqlEngineEdition == 4;

        _rbSchedulerAgent.Enabled = !isExpress;
        if (isExpress)
        {
            _lblEditionCheck.ForeColor = Color.DimGray;
            _lblEditionCheck.Text =
                "This instance is SQL Server Express, which does not include SQL Server Agent -- " +
                "use Windows Task Scheduler.";
            if (_rbSchedulerAgent.Checked)
                _rbSchedulerTask.Checked = true; // forces the radio group back to a valid choice
        }
        else if (_context?.SqlEngineEdition is int edition)
        {
            _lblEditionCheck.Text =
                $"SQL Server Agent is available on this instance (EngineEdition {edition}).";
        }
        else
        {
            _lblEditionCheck.Text =
                "Couldn't confirm the SQL Server edition (connection issue) -- SQL Server Agent " +
                "requires Standard or Enterprise; Express does not include it.";
        }
    }

    public override void OnEnter(DeploymentContext context)
    {
        _context = context;

        _rbSchedulerTask.Checked = context.Scheduler == SchedulerType.WindowsTaskScheduler;
        _rbSchedulerAgent.Checked = context.Scheduler == SchedulerType.SqlServerAgent;
        _rbSchedulerGranite.Checked = context.Scheduler == SchedulerType.GraniteScheduler;
        _rbSchedulerSkip.Checked = context.Scheduler == SchedulerType.None;
        UpdateSchedulerSectionVisibility();

        _cmbInterval.SelectedIndex = context.ScheduleIntervalMinutes switch
        {
            5 => 0,
            30 => 2,
            _ => 1
        };
        _txtAccount.Text = context.WindowsAccountName;
        _txtAccountPassword.Text = context.WindowsAccountPassword;
        _lblBootstrapResult.Text = string.Empty;
        SetBootstrapVisible(false);

        if (context.SqlEngineEdition.HasValue)
            ApplyEditionCheckResult(); // already known from an earlier visit -- show immediately
        else
            _ = DetectEditionAsync(context); // fire-and-forget; failure just leaves the option enabled
    }

    public override void OnLeave(DeploymentContext context)
    {
        context.Scheduler = _rbSchedulerAgent.Checked ? SchedulerType.SqlServerAgent
            : _rbSchedulerGranite.Checked ? SchedulerType.GraniteScheduler
            : _rbSchedulerSkip.Checked ? SchedulerType.None
            : SchedulerType.WindowsTaskScheduler;

        context.ScheduleIntervalMinutes = _cmbInterval.SelectedIndex switch
        {
            0 => 5,
            2 => 30,
            _ => 15
        };
        context.WindowsAccountName = NormalizeAccountName(_txtAccount.Text.Trim());
        context.WindowsAccountPassword = _txtAccountPassword.Text;
    }

    public override bool ValidateStep(DeploymentContext context, out string error)
    {
        // Nothing to validate on the skip path -- no mechanism is being
        // registered, so neither the edition check nor the Windows account
        // fields apply.
        if (_rbSchedulerSkip.Checked)
        {
            error = string.Empty;
            return true;
        }

        if (_rbSchedulerAgent.Checked && context.SqlEngineEdition == 4)
        {
            error = "This SQL Server instance is Express, which has no SQL Server Agent service -- " +
                    "choose Windows Task Scheduler instead.";
            return false;
        }

        // The account/password fields below only matter for the Windows
        // Task Scheduler path -- a SQL Server Agent job runs as its owner
        // (the Step 1 SQL login), and a Granite Scheduler job authenticates
        // however the Scheduler service itself does, so neither needs a
        // Windows account and there is nothing further to validate here.
        if (_rbSchedulerAgent.Checked || _rbSchedulerGranite.Checked)
        {
            error = string.Empty;
            return true;
        }

        string account = NormalizeAccountName(_txtAccount.Text.Trim());
        _txtAccount.Text = account;

        if (string.IsNullOrWhiteSpace(account)) { error = "Enter the Windows account to run the scheduled task as."; return false; }
        if (string.IsNullOrWhiteSpace(_txtAccountPassword.Text)) { error = "Enter the account's password."; return false; }

        // Checked here, synchronously, rather than only relying on the
        // Verify Account button -- this is what actually stops a bad
        // password from reaching Start Deployment. On the run that exposed
        // the gap, the deployment ran to completion (dozens of SQL batches)
        // before Task Scheduler rejected the account at the very last step.
        var result = WindowsCredentialValidator.Validate(account, _txtAccountPassword.Text);
        _lblVerifyResult.ForeColor = result.Success ? Color.SeaGreen : Color.Firebrick;
        _lblVerifyResult.Text = result.Message;

        if (!result.Success)
        {
            error = $"Windows rejected this account/password: {result.Message} Fix it above before continuing -- " +
                    "the scheduled task (and the SQL access below) both depend on it being correct.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
