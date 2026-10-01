using GraniteBiDeployWizard.Core;
using GraniteBiDeployWizard.Models;

namespace GraniteBiDeployWizard.UI;

public sealed class Step5DeployLogControl : WizardStepControl
{
    private readonly RichTextBox _console = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.Black,
        ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9.5F),
        BorderStyle = BorderStyle.FixedSingle
    };

    private readonly Button _btnStart = new() { Text = "Start Deployment", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(160, 32) };
    private readonly Button _btnCancel = new() { Text = "Cancel", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(100, 32), Enabled = false, Margin = new Padding(8, 0, 0, 0) };
    private readonly Label _lblStatus = new() { AutoSize = true, Margin = new Padding(12, 8, 0, 0) };

    // ----- Grant the deployment login full rights, every run -----------
    private readonly GroupBox _grpRights = new()
    {
        Text = "Deployment login rights (granted automatically, every run)",
        Width = 560,
        AutoSize = true,
        Padding = new Padding(12, 8, 12, 12),
        Margin = new Padding(0, 4, 0, 12)
    };

    private readonly RadioButton _rbRightsWindows = new()
    {
        Text = "Use Windows Authentication (the account this wizard is currently running as)",
        AutoSize = true,
        Checked = true
    };
    private readonly RadioButton _rbRightsSql = new() { Text = "Use a different SQL admin login", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly TextBox _txtRightsUsername = new() { Width = 300, Enabled = false };
    private readonly TextBox _txtRightsPassword = new() { Width = 300, UseSystemPasswordChar = true, Enabled = false };

    private CancellationTokenSource? _cts;
    private DeploymentContext? _context;

    public override string StepTitle => "Step 6 of 6: Deploy and Schedule";

    /// <summary>Raised when a deployment starts/finishes, so MainForm can lock navigation mid-run.</summary>
    public event EventHandler<bool>? RunningChanged;

    public Step5DeployLogControl()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(MakeHeading(StepTitle), 0, 0);

        BuildRightsPanel();
        root.Controls.Add(_grpRights, 0, 1);

        var buttonRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttonRow.Controls.Add(_btnStart);
        buttonRow.Controls.Add(_btnCancel);
        buttonRow.Controls.Add(_lblStatus);
        root.Controls.Add(buttonRow, 0, 2);

        root.Controls.Add(_console, 0, 3);

        Controls.Add(root);

        _btnStart.Click += async (_, _) => await RunDeploymentAsync();
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        _rbRightsWindows.CheckedChanged += (_, _) => UpdateRightsAuthFieldsEnabled();
        _rbRightsSql.CheckedChanged += (_, _) => UpdateRightsAuthFieldsEnabled();
    }

    private void BuildRightsPanel()
    {
        var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };

        inner.Controls.Add(MakeHint(
            "Three separate real deployments each turned up a different missing grant on the " +
            "Step 1 login -- read access on the source database, then CREATE PROCEDURE there for " +
            "Granite Scheduler. Every Start Deployment now begins by granting that login dbcreator " +
            "(so it can create the BI database if it doesn't exist yet) and db_owner on the source " +
            "(live) database, using the admin identity below -- entered once per run, live, never " +
            "stored. These rights stay standing afterward, not just for this run, so a later " +
            "redeploy to this same server shouldn't need this step again. Harmless to re-run: " +
            "granting a login something it already has does nothing."));

        inner.Controls.Add(_rbRightsWindows);
        inner.Controls.Add(_rbRightsSql);

        inner.Controls.Add(MakeFieldLabel("Admin SQL username:"));
        inner.Controls.Add(_txtRightsUsername);
        inner.Controls.Add(MakeFieldLabel("Admin password:"));
        inner.Controls.Add(_txtRightsPassword);
        inner.Controls.Add(MakeHint("That admin account needs the sysadmin role, or both securityadmin and db_owner of the source database -- either is enough to grant what's needed here."));

        _grpRights.Controls.Add(inner);
    }

    private void UpdateRightsAuthFieldsEnabled()
    {
        bool useSql = _rbRightsSql.Checked;
        _txtRightsUsername.Enabled = useSql;
        _txtRightsPassword.Enabled = useSql;
    }

    public override void OnEnter(DeploymentContext context)
    {
        _context = context;
    }

    public override void OnLeave(DeploymentContext context) { }

    public override bool ValidateStep(DeploymentContext context, out string error)
    {
        error = string.Empty;
        return true; // this is the last step; nothing to validate forward to
    }

    private async Task RunDeploymentAsync()
    {
        if (_context is null) return;

        if (_rbRightsSql.Checked && string.IsNullOrWhiteSpace(_txtRightsUsername.Text))
        {
            _lblStatus.ForeColor = Color.Firebrick;
            _lblStatus.Text = "Enter the admin SQL username above, or switch to Windows Authentication.";
            return;
        }

        _console.Clear();
        _btnStart.Enabled = false;
        _btnCancel.Enabled = true;
        RunningChanged?.Invoke(this, true);
        _cts = new CancellationTokenSource();

        try
        {
            var runner = new DeploymentRunner(AppendLog, ConfirmResync);
            if (_context.UsedDedicatedBootstrapLogin)
            {
                AppendLog(new LogEntry(LogLevel.Info,
                    $"Using dedicated login \"{_context.SqlUsername}\" created during setup for this deployment."));
            }
            if (_context.SourceViewsDeployedThisSession)
            {
                AppendLog(new LogEntry(LogLevel.Info,
                    $"The live database's dbo.vw_BI_* reporting views were deployed to [{_context.SourceDb}] during setup."));
            }
            AppendLog(new LogEntry(LogLevel.Info, $"Deploying to {_context.Server} ({_context.SourceDb} -> {_context.BiDb})."));

            string rightsAdminConnectionString = _rbRightsWindows.Checked
                ? SqlConnectionFactory.BuildIntegratedConnectionString(_context.Server, "master")
                : SqlConnectionFactory.BuildConnectionString(_context.Server, "master", _txtRightsUsername.Text.Trim(), _txtRightsPassword.Text);

            AppendLog(new LogEntry(LogLevel.Info, "Granting the deployment login full rights..."));
            var grantResult = await DeploymentRightsService.EnsureFullDeploymentRightsAsync(
                rightsAdminConnectionString, _context.SqlUsername, _context.SourceDb, _cts.Token);
            AppendLog(new LogEntry(grantResult.Success ? LogLevel.Success : LogLevel.Warning, grantResult.Message));
            // Non-fatal either way: if this failed (e.g. the admin identity
            // above isn't actually privileged enough), deployment proceeds
            // with whatever rights the login already had, and any
            // permission error downstream still gets its own clear,
            // actionable message, same as before this step existed.

            var result = await runner.RunAsync(_context, rightsAdminConnectionString, _cts.Token);

            if (result.Aborted)
            {
                // RunAsync already logged exactly why. Registering a scheduled
                // task or a .bat wrapper here would point at a sync that has
                // nowhere reliable to run against yet -- skip both and leave
                // the user with a clear, actionable log instead of a
                // misleadingly "complete" looking wizard.
                _lblStatus.ForeColor = Color.Firebrick;
                _lblStatus.Text = "Deployment aborted -- see log.";
            }
            else
            {
                // At most one scheduling mechanism gets registered, per the
                // choice made on Step 5 -- never both, even if the other
                // mechanism's script was separately ticked on Step 4.
                //
                // This is wrapped in its own try/catch, separate from the
                // outer one below: a real run showed why that matters --
                // 186 batches and a 3-minute initial sync had already
                // finished cleanly, and only the very last step (registering
                // Granite Scheduler) failed on a permissions gap, but it
                // still bubbled out to the generic "Deployment failed"
                // handler and made the whole run look like it had failed.
                // The data-deployment result above is unaffected by whether
                // scheduling registration succeeds, so its own status is
                // reported separately instead.
                bool schedulerRegistered = true;
                try
                {
                    switch (_context.Scheduler)
                    {
                        case SchedulerType.SqlServerAgent:
                            AppendLog(new LogEntry(LogLevel.Info,
                                $"Registering SQL Server Agent job '{_context.SqlAgentJobName}' (every {_context.ScheduleIntervalMinutes} min)..."));
                            await SqlAgentSchedulerService.RegisterOrUpdateSyncJobAsync(_context, _cts.Token);
                            AppendLog(new LogEntry(LogLevel.Success, "SQL Server Agent job registered."));
                            break;

                        case SchedulerType.GraniteScheduler:
                            AppendLog(new LogEntry(LogLevel.Info,
                                $"Registering Granite Scheduler job '{_context.GraniteSchedulerJobName}' (every {_context.ScheduleIntervalMinutes} min)..."));
                            await GraniteSchedulerService.RegisterOrUpdateSyncJobAsync(_context, _cts.Token);
                            AppendLog(new LogEntry(LogLevel.Success, "Granite Scheduler job registered."));
                            break;

                        case SchedulerType.None:
                            AppendLog(new LogEntry(LogLevel.Info, "Generating Run_BI_Sync.bat wrapper..."));
                            string skipBatchPath = BatchFileGenerator.WriteTo(_context, TaskFolder.Prepare(_context));
                            AppendLog(new LogEntry(LogLevel.Success, $"Wrote {skipBatchPath}"));
                            AppendLog(new LogEntry(LogLevel.Warning,
                                "Scheduling skipped, as chosen on Step 5 -- no Windows Scheduled Task or SQL Server " +
                                "Agent job was registered. The BI tables above are populated from the initial sync " +
                                "only and will not stay current until something runs Run_BI_Sync.bat (or " +
                                "bi.usp_RunSync directly) on a recurring basis."));
                            break;

                        default: // WindowsTaskScheduler
                            AppendLog(new LogEntry(LogLevel.Info, "Generating Run_BI_Sync.bat wrapper..."));
                            string batchPath = BatchFileGenerator.WriteTo(_context, TaskFolder.Prepare(_context));
                            AppendLog(new LogEntry(LogLevel.Success, $"Wrote {batchPath}"));

                            AppendLog(new LogEntry(LogLevel.Info,
                                $"Registering scheduled task '{_context.TaskName}' (every {_context.ScheduleIntervalMinutes} min)..."));
                            ScheduledTaskService.RegisterOrUpdateSyncTask(_context, batchPath);
                            AppendLog(new LogEntry(LogLevel.Success, "Scheduled task registered."));
                            break;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    schedulerRegistered = false;
                    AppendLog(new LogEntry(LogLevel.Error, DescribeSchedulerFailure(_context.Scheduler, ex)));
                }

                if (!schedulerRegistered)
                {
                    _lblStatus.ForeColor = Color.DarkOrange;
                    _lblStatus.Text = "Deployment complete, but scheduling could not be registered -- see log.";
                }
                else
                {
                    _lblStatus.ForeColor = result.BatchesFailed == 0 ? Color.SeaGreen : Color.DarkOrange;
                    _lblStatus.Text = result.BatchesFailed == 0
                        ? "Deployment complete."
                        : $"Deployment finished with {result.BatchesFailed} error(s) -- see log.";
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog(new LogEntry(LogLevel.Warning, "Deployment cancelled by user."));
            _lblStatus.ForeColor = Color.DarkOrange;
            _lblStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog(new LogEntry(LogLevel.Error, $"Deployment failed: {ex.Message}"));
            _lblStatus.ForeColor = Color.Firebrick;
            _lblStatus.Text = "Deployment failed.";
        }
        finally
        {
            _btnStart.Enabled = true;
            _btnCancel.Enabled = false;
            RunningChanged?.Invoke(this, false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Asked by DeploymentRunner only when bi.SyncLog shows the BI database
    /// already has data from a prior sync -- a redeploy, not a first-ever
    /// run. Runs on the UI thread (RunDeploymentAsync awaits RunAsync
    /// directly, no ConfigureAwait(false) anywhere in this codebase, so the
    /// WinForms SynchronizationContext keeps every continuation here),
    /// which is why a blocking MessageBox.Show is safe to call straight
    /// from this callback.
    /// </summary>
    private bool ConfirmResync(DeploymentRunner.SyncSkipPromptContext prompt)
    {
        string ageText = prompt.MinutesAgo <= 0 ? "less than a minute ago" : $"{prompt.MinutesAgo} minute(s) ago";
        string scheduleText = prompt.Scheduler == SchedulerType.None
            ? "No scheduler is registered for this deployment -- skipping means the BI tables stay as they are until the sync is run again by hand."
            : $"the scheduled task (every {prompt.ScheduleIntervalMinutes} min) will bring it fully current on its own shortly after.";

        var result = MessageBox.Show(
            this,
            $"The BI database already has synced data -- the last completed sync finished {ageText} " +
            $"({prompt.LastSyncStatus}).\n\n" +
            "Re-running the full sync now can take several minutes on a large database. If you skip it, " +
            $"{scheduleText}\n\n" +
            "Re-sync everything now?",
            "Skip the initial sync?",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        return result == DialogResult.Yes;
    }

    /// <summary>
    /// Turns a raw scheduler-registration exception into an actionable log
    /// line -- what mechanism failed, why, and what to do about it. The data
    /// deployment above already finished by the time this can run, so every
    /// message says as much: fixing the cause and clicking Start Deployment
    /// again is safe, not a redo of work that's already done.
    /// </summary>
    private static string DescribeSchedulerFailure(SchedulerType scheduler, Exception ex)
    {
        string baseMessage = ex.Message.TrimEnd('.', ' ');

        if (scheduler == SchedulerType.GraniteScheduler &&
            ex.Message.Contains("CREATE PROCEDURE permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "Could not register the Granite Scheduler job: the deployment login doesn't have " +
                   "CREATE PROCEDURE permission in the live database, needed to create the proxy " +
                   "procedure (dbo.usp_RunGraniteBiSync). This should be fixed automatically the next " +
                   "time you click Start Deployment -- the rights-granting step above runs every time " +
                   "-- so check the admin identity entered there actually has sysadmin/securityadmin " +
                   "rights on this server, then click Start Deployment again; the data above already " +
                   "deployed successfully and will just be refreshed, not redone from scratch. Or go " +
                   "back to Step 5 and choose Windows Task Scheduler or SQL Server Agent instead, " +
                   "neither of which needs this permission.";
        }

        string mechanismLabel = scheduler switch
        {
            SchedulerType.SqlServerAgent => "the SQL Server Agent job",
            SchedulerType.GraniteScheduler => "the Granite Scheduler job",
            _ => "the scheduled task"
        };

        return $"Could not register {mechanismLabel}: {baseMessage}. The data above already deployed " +
               "successfully -- fix the issue above and click Start Deployment again once it's resolved.";
    }

    private void AppendLog(LogEntry entry)
    {
        if (_console.InvokeRequired)
        {
            _console.BeginInvoke(new MethodInvoker(() => AppendLog(entry)));
            return;
        }

        Color color = entry.Level switch
        {
            LogLevel.Success => Color.LightGreen,
            LogLevel.Warning => Color.Khaki,
            LogLevel.Error => Color.LightSalmon,
            _ => Color.Gainsboro
        };

        _console.SelectionStart = _console.TextLength;
        _console.SelectionLength = 0;
        _console.SelectionColor = color;
        _console.AppendText($"[{entry.Timestamp:HH:mm:ss}] {entry.Message}{Environment.NewLine}");
        _console.ScrollToCaret();
    }
}
