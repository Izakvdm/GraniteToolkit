using GraniteBiDeployWizard.Core;
using GraniteBiDeployWizard.Models;

namespace GraniteBiDeployWizard.UI;

/// <summary>
/// Confirms the live source database has the dbo.vw_BI_* reporting views
/// this deployment kit reads from but never creates, and deploys them if
/// not, before the wizard goes any further.
/// </summary>
/// <remarks>
/// This started as a collapsible aside on Panel 1 and was promoted to its
/// own required step. A missed reporting-views prerequisite doesn't fail
/// where you'd expect it to -- Panel 1's Test Connection and every check up
/// to Panel 5 all succeed, and the actual failure (95 "Invalid object name"
/// errors) only shows up deep into Panel 6's deployment run, after CREATE
/// DATABASE and every bi.* table build have already completed. That gap is
/// exactly what cost three full deployment attempts to trace back to this
/// on a real environment (see the README). A collapsible section on Panel 1
/// was easy to miss or dismiss; a dedicated step with its own Next-blocking
/// ValidateStep is not.
/// </remarks>
public sealed class Step2ReportingViewsControl : WizardStepControl
{
    private readonly TextBox _txtSourceDb = new() { Width = 300, Text = "GraniteLive" };
    private readonly Button _btnCheckNow = new() { Text = "Check Now", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(110, 0), Margin = new Padding(0, 10, 0, 0) };
    private readonly Label _lblStatus = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 8, 0, 0) };

    private readonly TextBox _txtScriptPath = new() { Width = 360, ReadOnly = true };
    private readonly Button _btnBrowseScript = new() { Text = "Browse...", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(100, 0), Margin = new Padding(6, 0, 0, 0) };
    private readonly CheckBox _chkWindowsAuth = new()
    {
        Text = "Use Windows Authentication (the account this wizard is running as) instead of the Step 1 login",
        AutoSize = true,
        Margin = new Padding(0, 6, 0, 0)
    };
    private readonly Button _btnDeploy = new() { Text = "Deploy Views to Source Database", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(240, 0), Margin = new Padding(0, 10, 0, 0) };
    private readonly Label _lblDeployResult = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 8, 0, 0) };

    public override string StepTitle => "Step 2 of 6: Live Database Reporting Views";

    // This step has no server/login fields of its own -- it uses whatever
    // Step 1 already collected, cached here on OnEnter.
    private string _server = string.Empty;
    private string _sqlUsername = string.Empty;
    private string _sqlPassword = string.Empty;

    private SourceViewsPrerequisiteService.CheckResult? _lastCheck;
    private string _lastCheckServer = string.Empty;
    private string _lastCheckSourceDb = string.Empty;
    private bool _deployedThisSession;

    public Step2ReportingViewsControl()
    {
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true
        };

        layout.Controls.Add(MakeHeading(StepTitle));
        layout.Controls.Add(MakeHint(
            "This deployment reads $(SourceDb).dbo.vw_BI_* views that must already exist in the " +
            "live GraniteWMS database -- this BI kit does not create them; a separate base-reporting-" +
            "views script does. On a fresh or sandbox copy of the live database that never had that " +
            "script run against it, deployment gets all the way through CREATE DATABASE and then " +
            "fails with \"Invalid object name\" for every bi.* table, deep into Panel 6's deploy run -- " +
            "a much more expensive, confusing failure to hit than catching it here. This check is " +
            "required: Next is blocked until it runs and either finds the views or you deploy them below."));

        layout.Controls.Add(MakeFieldLabel("Source database (the live GraniteWMS database):"));
        layout.Controls.Add(_txtSourceDb);

        var checkRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        checkRow.Controls.Add(_btnCheckNow);
        layout.Controls.Add(checkRow);
        layout.Controls.Add(_lblStatus);

        layout.Controls.Add(MakeFieldLabel("Base reporting-views script (e.g. Granite_Superset_Views.sql):"));
        var fileRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        fileRow.Controls.Add(_txtScriptPath);
        fileRow.Controls.Add(_btnBrowseScript);
        layout.Controls.Add(fileRow);
        layout.Controls.Add(_chkWindowsAuth);
        layout.Controls.Add(MakeHint(
            "Creating views needs more than a read-only grant, so this normally needs an admin login " +
            "-- tick Windows Authentication if the account running this wizard has that access on this " +
            "server, otherwise it uses the login from Step 1."));

        layout.Controls.Add(_btnDeploy);
        layout.Controls.Add(_lblDeployResult);

        Controls.Add(layout);

        _btnCheckNow.Click += async (_, _) => await RunCheckAsync();
        _btnBrowseScript.Click += (_, _) => BrowseForScript();
        _btnDeploy.Click += async (_, _) => await DeployAsync();
    }

    private void BrowseForScript()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select the base reporting-views script",
            Filter = "SQL scripts (*.sql)|*.sql|All files (*.*)|*.*",
            FileName = _txtScriptPath.Text
        };

        if (dialog.ShowDialog(FindForm()) == DialogResult.OK)
            _txtScriptPath.Text = dialog.FileName;
    }

    private async Task RunCheckAsync()
    {
        string sourceDb = _txtSourceDb.Text.Trim();

        if (string.IsNullOrWhiteSpace(_server) || string.IsNullOrWhiteSpace(sourceDb))
        {
            _lblStatus.ForeColor = Color.Firebrick;
            _lblStatus.Text = "Go back to Step 1 and enter the server, and enter the source database name above.";
            return;
        }

        _btnCheckNow.Enabled = false;
        _lblStatus.ForeColor = Color.DimGray;
        _lblStatus.Text = "Checking...";

        string connectionString = SqlConnectionFactory.BuildConnectionString(_server, "master", _sqlUsername, _sqlPassword);
        var result = await SourceViewsPrerequisiteService.CheckAsync(connectionString, sourceDb);

        _lblStatus.ForeColor = !result.Success ? Color.Firebrick : result.ViewsFound == 0 ? Color.DarkOrange : Color.SeaGreen;
        _lblStatus.Text = result.Message;
        _btnCheckNow.Enabled = true;

        // Recorded against this exact server + source database so
        // ValidateStep can tell a stale check (fields edited since) from a
        // current one.
        _lastCheck = result;
        _lastCheckServer = _server;
        _lastCheckSourceDb = sourceDb;
    }

    private async Task DeployAsync()
    {
        string sourceDb = _txtSourceDb.Text.Trim();
        string scriptPath = _txtScriptPath.Text.Trim();

        if (string.IsNullOrWhiteSpace(_server) || string.IsNullOrWhiteSpace(sourceDb))
        {
            ShowDeployError("Go back to Step 1 and enter the server, and enter the source database name above.");
            return;
        }
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            ShowDeployError("Select the base reporting-views script file first.");
            return;
        }

        _btnDeploy.Enabled = false;
        _lblDeployResult.ForeColor = Color.DimGray;
        _lblDeployResult.Text = $"Deploying {Path.GetFileName(scriptPath)} to [{sourceDb}]...";

        string connectionString = _chkWindowsAuth.Checked
            ? SqlConnectionFactory.BuildIntegratedConnectionString(_server, "master")
            : SqlConnectionFactory.BuildConnectionString(_server, "master", _sqlUsername, _sqlPassword);

        try
        {
            var result = await SourceViewsPrerequisiteService.DeployAsync(connectionString, sourceDb, scriptPath);

            if (result.BatchesFailed == 0)
            {
                _lblDeployResult.ForeColor = Color.SeaGreen;
                _lblDeployResult.Text = $"Deployed: {result.BatchesExecuted} batch(es) executed in [{sourceDb}].";
                _deployedThisSession = true;
            }
            else
            {
                _lblDeployResult.ForeColor = Color.DarkOrange;
                _lblDeployResult.Text =
                    $"Ran with {result.BatchesFailed} error(s) out of {result.BatchesExecuted + result.BatchesFailed} " +
                    $"batch(es): {string.Join(" | ", result.Errors.Take(3))}";
            }

            // Re-check so the status line above reflects reality rather than
            // staying on whatever prompted the deploy.
            await RunCheckAsync();
        }
        catch (Exception ex)
        {
            ShowDeployError($"Could not deploy: {ex.Message}");
        }
        finally
        {
            _btnDeploy.Enabled = true;
        }
    }

    private void ShowDeployError(string message)
    {
        _lblDeployResult.ForeColor = Color.Firebrick;
        _lblDeployResult.Text = message;
    }

    public override async void OnEnter(DeploymentContext context)
    {
        _server = context.Server;
        _sqlUsername = context.SqlUsername;
        _sqlPassword = context.SqlPassword;
        _txtSourceDb.Text = context.SourceDb;
        _lblDeployResult.Text = string.Empty;

        bool stillCurrent = _lastCheck is not null
            && string.Equals(_lastCheckServer, _server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_lastCheckSourceDb, _txtSourceDb.Text.Trim(), StringComparison.OrdinalIgnoreCase);

        if (stillCurrent)
        {
            var check = _lastCheck!;
            _lblStatus.ForeColor = !check.Success ? Color.Firebrick : check.ViewsFound == 0 ? Color.DarkOrange : Color.SeaGreen;
            _lblStatus.Text = check.Message;
        }
        else
        {
            // Auto-run on arrival, same as the check used to auto-run right
            // after Test Connection on Panel 1 -- the whole point of this
            // step is this check, so it shouldn't need an extra click in the
            // common case. async void is safe here: MainForm.ShowStep calls
            // OnEnter and immediately displays the panel; the check finishes
            // in the background and updates the label when done, same
            // pattern as every button click handler in this wizard.
            _lblStatus.Text = string.Empty;
            await RunCheckAsync();
        }
    }

    public override void OnLeave(DeploymentContext context)
    {
        context.SourceDb = _txtSourceDb.Text.Trim();
        context.SourceViewsDeployedThisSession = context.SourceViewsDeployedThisSession || _deployedThisSession;
        context.SourceViewsCheckedForDb = _lastCheckSourceDb;
        context.SourceViewsFoundCount = _lastCheck?.ViewsFound;
    }

    public override bool ValidateStep(DeploymentContext context, out string error)
    {
        string sourceDb = _txtSourceDb.Text.Trim();
        if (string.IsNullOrWhiteSpace(sourceDb))
        {
            error = "Enter the source database name.";
            return false;
        }

        bool checkedThisCombo = _lastCheck is not null
            && string.Equals(_lastCheckServer, _server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_lastCheckSourceDb, sourceDb, StringComparison.OrdinalIgnoreCase);

        if (!checkedThisCombo)
        {
            error = $"Click \"Check Now\" to confirm [{sourceDb}] has its dbo.vw_BI_* reporting views before continuing.";
            return false;
        }

        if (!_lastCheck!.Success)
        {
            error = $"Could not confirm [{sourceDb}]'s reporting views ({_lastCheck.Message}). " +
                    "Fix that first, or retry \"Check Now\", before continuing.";
            return false;
        }

        if (_lastCheck.ViewsFound == 0 && !_deployedThisSession)
        {
            error = $"[{sourceDb}] has no dbo.vw_BI_* reporting views yet -- deploy them above before continuing.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
