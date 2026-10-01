using GraniteAttachInstaller.Core;
using GraniteAttachInstaller.Models;

namespace GraniteAttachInstaller.UI;

public sealed class Step3InstallControl : WizardStepControl
{
    public override string StepTitle => "Step 3 of 3: Review and install";

    public event EventHandler<bool>? RunningChanged;

    private readonly Label _lblSummary = new() { AutoSize = true, MaximumSize = new Size(680, 0), Margin = new Padding(0, 0, 0, 12) };
    private readonly Button _btnInstall = MakeButton("Install", 140);
    private readonly TextBox _txtLog = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Width = 700,
        Height = 360,
        Font = new Font("Consolas", 9F),
        Margin = new Padding(0, 12, 0, 0)
    };

    private InstallContext? _context;
    private CancellationTokenSource? _cts;

    public Step3InstallControl()
    {
        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));
        page.Controls.Add(_lblSummary);
        page.Controls.Add(MakeRow(_btnInstall));
        page.Controls.Add(_txtLog);
        Controls.Add(page);

        _btnInstall.Click += async (_, _) => await RunInstallAsync();
    }

    public override void OnEnter(InstallContext context)
    {
        _context = context;
        string sqlFolder = FindSqlFolder(context.ProjectPath);
        context.SqlFolder = sqlFolder;

        _lblSummary.Text =
            $"Server:        {context.SqlServerInstance}\n" +
            $"Database:      {context.DatabaseName}\n" +
            $"Auth:          {(context.UseSqlAuth ? $"SQL login ({context.SqlUser})" : "Windows")}\n" +
            $"Project:       {context.ProjectPath}\n" +
            $"SQL scripts:   {(string.IsNullOrEmpty(sqlFolder) ? "NOT FOUND - fix the project path on Step 2" : sqlFolder)}\n" +
            $"IIS site:      {context.SiteName}  (port {context.Port}, pool {context.AppPoolName})\n" +
            $"Physical path: {context.PhysicalPath}\n" +
            $"Process name:  {(string.IsNullOrWhiteSpace(context.ProcessName) ? "(none - tables only)" : context.ProcessName)}\n" +
            $"Public URL:    {context.PublicBaseUrl}\n\n" +
            "Click Install to run the database setup, publish the app, and configure IIS.";
    }

    private static string FindSqlFolder(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return string.Empty;
        try
        {
            // GraniteAttach.csproj lives in <root>/app/GraniteAttach/ - the sql
            // folder is <root>/sql.
            string? appDir = Path.GetDirectoryName(projectPath);
            string? rootDir = appDir is null ? null : Directory.GetParent(appDir)?.Parent?.FullName;
            if (rootDir is null) return string.Empty;
            string candidate = Path.Combine(rootDir, "sql");
            return Directory.Exists(candidate) ? candidate : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void Log(LogEntry entry)
    {
        if (InvokeRequired) { BeginInvoke(new MethodInvoker(() => Log(entry))); return; }
        _txtLog.AppendText($"{entry.Timestamp:HH:mm:ss} {entry.Prefix} {entry.Message}\r\n");
    }

    private async Task RunInstallAsync()
    {
        if (_context is null) return;
        if (string.IsNullOrEmpty(_context.SqlFolder))
        {
            MessageBox.Show(this, "Couldn't find the sql/ folder next to the project - go back to Step 2 and check the project path.", "Missing sql folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnInstall.Enabled = false;
        RunningChanged?.Invoke(this, true);
        _txtLog.Clear();
        _cts = new CancellationTokenSource();

        try
        {
            var dbInstaller = new AttachDatabaseInstaller(Log);
            await dbInstaller.RunAsync(_context, _cts.Token);

            var appDeployer = new AttachAppDeployer(Log);
            await appDeployer.StopExistingIisAsync(_context, _cts.Token);
            await appDeployer.PublishAsync(_context, _cts.Token);
            appDeployer.WriteAppSettings(_context);
            await appDeployer.SetUpIisAsync(_context, _cts.Token);

            Log(new LogEntry(LogLevel.Stage, "Done."));
            Log(new LogEntry(LogLevel.Success, $"Granite Attach should now be reachable at {_context.PublicBaseUrl}"));
            if (!string.IsNullOrWhiteSpace(_context.ResolvedWebTemplatePath))
                Log(new LogEntry(LogLevel.Info, $"Paste the WebTemplate from: {_context.ResolvedWebTemplatePath}"));
            if (!string.IsNullOrWhiteSpace(_context.ProcessName))
            {
                Log(new LogEntry(LogLevel.Info, "Next, in Process Designer, build (or add to) the process with these steps:"));
                Log(new LogEntry(LogLevel.Info, $"  AttachStart  (visible, Required=No) -> PreScript: dbo.Prescript_{_context.ProcessName}_AttachStart"));
                Log(new LogEntry(LogLevel.Info, "  AttachToken  (hidden, index 100)    -> default value sourced from AttachStart's output"));
                Log(new LogEntry(LogLevel.Info, $"  AttachShow   (visible, Required=No) -> PreScript: dbo.Prescript_{_context.ProcessName}_AttachShow, WebTemplate: the file above"));
                Log(new LogEntry(LogLevel.Info, $"  Step200      (post-exec, 200-series) -> PreScript: dbo.Prescript_{_context.ProcessName}_Step200"));
                Log(new LogEntry(LogLevel.Info, "See DEMO_SETUP.md at the project root for a fully worked example."));
            }
            else
            {
                Log(new LogEntry(LogLevel.Info, "No process name was given, so only the tables were set up. Run this installer again with a process name once you've decided what to call it."));
            }
        }
        catch (Exception ex)
        {
            Log(new LogEntry(LogLevel.Error, ex.Message));
            MessageBox.Show(this, $"Install stopped: {ex.Message}\n\nSee the log for details.", "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnInstall.Enabled = true;
            RunningChanged?.Invoke(this, false);
        }
    }

    public override bool ValidateStep(InstallContext context, out string error)
    {
        error = string.Empty;
        return true;
    }
}
