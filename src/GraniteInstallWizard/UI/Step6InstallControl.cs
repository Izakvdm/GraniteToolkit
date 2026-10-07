using System.Diagnostics;
using System.Text;
using GraniteInstallWizard.Core;
using GraniteInstallWizard.Models;

namespace GraniteInstallWizard.UI;

public sealed class Step6InstallControl : WizardStepControl
{
    private readonly TextBox _txtReview = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9F),
        BackColor = Color.FromArgb(248, 248, 248),
        Dock = DockStyle.Fill
    };

    private readonly CheckBox _chkDryRun = new()
    {
        Text = "Dry run: run every check and log what would change, without changing anything",
        AutoSize = true,
        Margin = new Padding(0, 8, 0, 4)
    };

    private readonly Button _btnStart = MakeButton("Start Install", 140, new Padding(0, 4, 8, 0));
    private readonly Button _btnCancel = MakeButton("Cancel", 100, new Padding(0, 4, 8, 0));
    private readonly Button _btnSaveProfile = MakeButton("Save profile...", 120, new Padding(0, 4, 8, 0));
    private readonly Button _btnOpenLog = MakeButton("Open log", 100, new Padding(0, 4, 8, 0));
    private readonly Label _lblStatus = new() { AutoSize = true, Margin = new Padding(8, 10, 0, 0), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };

    private readonly ListView _stages = new()
    {
        View = View.Details,
        HeaderStyle = ColumnHeaderStyle.None,
        FullRowSelect = true,
        Dock = DockStyle.Fill
    };

    private readonly RichTextBox _console = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.Black,
        ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9F),
        BorderStyle = BorderStyle.FixedSingle,
        WordWrap = true // v0.3.2: long pre-flight messages were cut off at the right edge
    };

    private InstallContext? _context;
    private CancellationTokenSource? _cts;
    private string? _lastLogFile;
    private readonly string _wizardVersion;

    public override string StepTitle => "Step 6 of 6: Review and Install";

    /// <summary>Raised when an install starts/finishes, so MainForm can lock navigation mid-run.</summary>
    public event EventHandler<bool>? RunningChanged;

    public Step6InstallControl(string wizardVersion)
    {
        _wizardVersion = wizardVersion;
        _btnCancel.Enabled = false;
        _btnOpenLog.Enabled = false;

        _stages.Columns.Add("State", 44);
        _stages.Columns.Add("Stage", 230);
        foreach (string name in InstallRunner.StageNames)
            _stages.Items.Add(new ListViewItem(new[] { string.Empty, name }));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));

        root.Controls.Add(MakeHeading(StepTitle), 0, 0);
        root.Controls.Add(_txtReview, 0, 1);
        root.Controls.Add(_chkDryRun, 0, 2);
        root.Controls.Add(MakeRow(_btnStart, _btnCancel, _btnSaveProfile, _btnOpenLog, _lblStatus), 0, 3);

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 8, 0, 0) };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 290));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        split.Controls.Add(_stages, 0, 0);
        split.Controls.Add(_console, 1, 0);
        root.Controls.Add(split, 0, 4);
        Controls.Add(root);

        _chkDryRun.CheckedChanged += (_, _) => _btnStart.Text = _chkDryRun.Checked ? "Start Dry Run" : "Start Install";
        _btnStart.Click += async (_, _) => await RunAsync();
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        _btnSaveProfile.Click += (_, _) => SaveProfile();
        _btnOpenLog.Click += (_, _) =>
        {
            if (_lastLogFile is not null && File.Exists(_lastLogFile))
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_lastLogFile}\"") { UseShellExecute = true });
        };
    }

    public override void OnEnter(InstallContext context)
    {
        _context = context;
        _txtReview.Text = BuildReview(context);
    }

    public override bool ValidateStep(InstallContext context, out string error)
    {
        error = string.Empty;
        return true; // last step; Next becomes Close
    }

    private static string BuildReview(InstallContext c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RELEASE AND TARGET");
        sb.AppendLine($"  Release folder   {c.ReleaseFolder}");
        sb.AppendLine($"  Install folder   {c.InstallRoot}");
        sb.AppendLine($"  Company          {c.CompanyName}");
        sb.AppendLine($"  Date format      {c.DateFormat}  (Business API: {DateFormatConverter.ToDotNet(c.DateFormat)})");
        sb.AppendLine();
        sb.AppendLine("PREREQUISITES (installed only if missing)");
        sb.AppendLine($"  IIS features: {Yn(c.InstallIisFeatures)}   URL Rewrite: {Yn(c.InstallUrlRewrite)}   .NET 8 Hosting: {Yn(c.InstallDotNet8Hosting)}   .NET 6 Hosting: {Yn(c.InstallDotNet6Hosting)}");
        sb.AppendLine();
        sb.AppendLine("DATABASE");
        string auth = c.SqlAuth == SqlAuthMode.Windows ? "Windows authentication" : $"SQL login {c.SqlAdminUser}";
        sb.AppendLine($"  {c.DatabaseName} on {c.SqlServer} (connecting with {auth})");
        sb.AppendLine(c.DatabaseMode == DatabaseMode.CreateNew
            ? "  Mode             create a new, clean database"
            : "  Mode             use the existing database as it is (create script not run, data kept)");
        sb.AppendLine($"  App login        {c.AppLogin} (db_owner)");
        if (c.ResetExistingAppLoginPassword)
            sb.AppendLine($"  !! If {c.AppLogin} already exists with another password, its password WILL BE CHANGED");
        if (c.DatabaseMode == DatabaseMode.CreateNew && c.DropExistingDatabase && c.LastSqlCheck?.DatabaseExists == true)
            sb.AppendLine($"  !! The existing {c.DatabaseName} database WILL BE DROPPED");
        var dbHotfix = HotfixScripts.Find(c);
        bool runDbHotfix = c.ApplyDatabaseHotfix && dbHotfix.Count > 0;
        sb.AppendLine(dbHotfix.Count == 0
            ? "  Hotfix scripts   None in this release"
            : $"  Hotfix scripts   {Yn(runDbHotfix)}  ({HotfixScripts.Describe(dbHotfix)}){(c.DatabaseMode == DatabaseMode.UseExisting && runDbHotfix ? "  changes objects in the existing database: back it up first" : "")}");
        var appHotfix = GraniteComponent.CoreStack.Where(comp => c.IsEnabled(comp.Key) && c.HotfixPathFor(comp) is not null).Select(comp => comp.Title).ToList();
        sb.AppendLine(appHotfix.Count == 0
            ? "  Hotfix app files None in this release"
            : $"  Hotfix app files {Yn(c.ApplyHotfix)}  ({string.Join(", ", appHotfix)})");
        if (c.IsEnabled(GraniteComponent.Custodian))
        {
            var source = c.CustodianTokenSource;
            sb.AppendLine(source is null
                ? "  !! Custodian token  none: " + CustodianToken.NoSourceText
                : c.DatabaseMode == DatabaseMode.CreateNew || c.CustodianTokenFile.Length > 0
                    ? $"  Custodian token  set from {source.Value.Label}{(c.DatabaseMode == DatabaseMode.UseExisting ? " (replaces the database's token)" : "")}"
                    : $"  Custodian token  added from {source.Value.Label} only if the database has none");
            sb.AppendLine("  Report Server    found on this server and set as Custodian's SSRSWebServiceUrl, only if that's empty");
        }
        sb.AppendLine();
        sb.AppendLine("WEBSITES (HTTPS)");
        foreach (var comp in c.EnabledComponents)
            sb.AppendLine($"  {comp.Title,-14} {c.Sites[comp.Key].SiteName,-24} {c.UrlFor(comp.Key)}");
        sb.AppendLine($"  Firewall rules   {Yn(c.OpenFirewall)}");
        if (c.InstallAlongside)
            sb.AppendLine("  Alongside        yes: existing Granite sites, ports and folders are left as they are");
        if (c.ReplaceExistingSites)
            sb.AppendLine("  !! Existing IIS sites with these names WILL BE REMOVED and recreated (their folders are kept as .bak)");
        sb.AppendLine();
        sb.AppendLine("CERTIFICATE");
        if (c.CertMode == CertificateMode.CreateSelfSigned)
        {
            sb.AppendLine($"  New self-signed \"{c.CertFriendlyName}\" for {string.Join(", ", c.CertDnsNames.Concat(c.CertIpAddresses))}");
            sb.AppendLine($"  Trust on this server: {Yn(c.TrustCertificate)}");
        }
        else
        {
            sb.AppendLine($"  Existing certificate {c.ExistingCertThumbprint}");
        }
        return sb.ToString();
    }

    private static string Yn(bool b) => b ? "yes" : "no";

    private void SaveProfile()
    {
        if (_context is null) return;
        using var dialog = new SaveFileDialog { Filter = "Install profile (*.json)|*.json", FileName = "GraniteInstallProfile.json", Title = "Save install profile (no passwords)" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, InstallProfile.From(_context, _wizardVersion).ToJson());
        ShowResult(_lblStatus, "Profile saved (passwords are never saved).", Color.SeaGreen);
    }

    private async Task RunAsync()
    {
        if (_context is null) return;
        _context.DryRun = _chkDryRun.Checked;

        if (!_context.DryRun)
        {
            var answer = MessageBox.Show(this,
                "Start the install now? This changes IIS, SQL Server, certificates and files on this server.\n\n" +
                "Tip: tick Dry run first to check everything without changing anything.",
                "Start install", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
        }

        _console.Clear();
        foreach (ListViewItem item in _stages.Items) { item.Text = string.Empty; item.ForeColor = Color.Black; }
        _btnStart.Enabled = false;
        _chkDryRun.Enabled = false;
        _btnCancel.Enabled = true;
        ShowResult(_lblStatus, _context.DryRun ? "Dry run in progress..." : "Installing...", Color.DimGray);
        RunningChanged?.Invoke(this, true);
        _cts = new CancellationTokenSource();

        try
        {
            var runner = new InstallRunner(AppendLog, SetStage, _wizardVersion);
            InstallResult result = await runner.RunAsync(_context, _cts.Token);
            _lastLogFile = result.LogFile;
            _btnOpenLog.Enabled = true;

            if (result.Cancelled)
                ShowResult(_lblStatus, "Cancelled.", Color.DarkOrange);
            else if (!result.Succeeded)
                ShowResult(_lblStatus, _context.DryRun
                    ? "The dry run found problems (see the log). Nothing was changed."
                    : "Stopped with an error; see the log. Fix it and run again.", Color.Firebrick);
            else if (_context.DryRun)
                ShowResult(_lblStatus, "Dry run passed. Nothing was changed. Untick Dry run to install.", Color.SeaGreen);
            else if (result.VerifyFailed)
                ShowResult(_lblStatus, "Installed, but some checks failed; see the log.", Color.DarkOrange);
            else
                ShowResult(_lblStatus, "GraniteWMS is installed.", Color.SeaGreen);

            if (result.Succeeded && result.SummaryText.Length > 0)
            {
                AppendLog(new LogEntry(LogLevel.Info, string.Empty));
                foreach (string line in result.SummaryText.Split(Environment.NewLine))
                    AppendLog(new LogEntry(LogLevel.Success, line));
            }
            if (result.RestartNeeded)
                AppendLog(new LogEntry(LogLevel.Warning, "Windows asked for a restart during the prerequisites stage. Restart the server before go-live."));
        }
        catch (Exception ex)
        {
            // e.g. the log folder couldn't be made admin-only (WizardDataFolder): nothing was changed.
            AppendLog(new LogEntry(LogLevel.Error, ex.Message));
            ShowResult(_lblStatus, "Couldn't start: " + ex.Message, Color.Firebrick);
        }
        finally
        {
            _btnStart.Enabled = true;
            _chkDryRun.Enabled = true;
            _btnCancel.Enabled = false;
            RunningChanged?.Invoke(this, false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void SetStage(int index, StageState state)
    {
        if (InvokeRequired) { BeginInvoke(new MethodInvoker(() => SetStage(index, state))); return; }
        var item = _stages.Items[index];
        (item.Text, item.ForeColor) = state switch
        {
            StageState.Running => ("  >", Color.DodgerBlue),
            StageState.Done => (" OK", Color.SeaGreen),
            StageState.Failed => ("  X", Color.Firebrick),
            StageState.Skipped => ("  -", Color.DimGray),
            _ => (string.Empty, Color.Black)
        };
        item.EnsureVisible();
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
            LogLevel.Detail => Color.Gray,
            LogLevel.DryRun => Color.Plum,
            LogLevel.Stage => Color.DeepSkyBlue,
            _ => Color.Gainsboro
        };

        _console.SelectionStart = _console.TextLength;
        _console.SelectionLength = 0;
        _console.SelectionColor = color;
        _console.AppendText(entry.Message.Length == 0 ? Environment.NewLine : $"[{entry.Timestamp:HH:mm:ss}] {entry.Message}{Environment.NewLine}");
        _console.ScrollToCaret();
    }
}
