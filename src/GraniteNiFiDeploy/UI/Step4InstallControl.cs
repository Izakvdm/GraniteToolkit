using System.Diagnostics;
using GraniteNiFiDeploy.Core;
using GraniteNiFiDeploy.Models;

namespace GraniteNiFiDeploy.UI;

public sealed class Step4InstallControl : WizardStepControl
{
    public override string StepTitle => "Step 4 of 4: Review and install";

    public event EventHandler<bool>? RunningChanged;

    private readonly Label _lblSummary = new() { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 0, 0, 12), Font = new Font("Consolas", 9F) };
    private readonly Button _btnInstall = MakeButton("Install", 140);
    private readonly Button _btnOpenNiFi = MakeButton("Open NiFi", 110);
    private readonly Button _btnOpenInbound = MakeButton("Open import folder", 140);
    private readonly Button _btnOpenLog = MakeButton("Open log", 100);
    private readonly RichTextBox _txtLog = new()
    {
        ReadOnly = true,
        Width = 720,
        Height = 340,
        Font = new Font("Consolas", 9F),
        BackColor = Color.White,
        Margin = new Padding(0, 12, 0, 0),
        DetectUrls = false
    };

    private DeployContext? _context;
    private CancellationTokenSource? _cts;
    private StreamWriter? _logFile;
    private string? _logPath;
    private bool _done;

    public Step4InstallControl()
    {
        var page = MakePage();
        page.Controls.Add(MakeHeading(StepTitle));
        page.Controls.Add(_lblSummary);
        page.Controls.Add(MakeRow(_btnInstall, _btnOpenNiFi, _btnOpenInbound, _btnOpenLog));
        page.Controls.Add(_txtLog);
        Controls.Add(page);

        _btnOpenNiFi.Visible = _btnOpenInbound.Visible = _btnOpenLog.Visible = false;
        _btnInstall.Click += async (_, _) => await RunAsync();
        _btnOpenNiFi.Click += (_, _) => { if (_context is not null) Open(_context.NiFiUrl); };
        _btnOpenInbound.Click += (_, _) => { if (_context is not null) Open(_context.InboundFolder); };
        _btnOpenLog.Click += (_, _) => { if (_logPath is not null) Open(_logPath); };
    }

    public override void OnEnter(DeployContext c)
    {
        _context = c;
        var m = c.Media!;
        _lblSummary.Text =
            $"NiFi           {m.NiFiVersion} with Java {m.JavaVersion}, JDBC {m.JdbcJarName}\n" +
            $"Installs to    {c.NiFiHome}\n" +
            $"Service        {c.ServiceName}, https://localhost:{c.Port}/nifi, heap {c.HeapSize}\n" +
            $"NiFi user      {c.AdminUser}\n" +
            $"Import folder  {c.ImportRoot}  (Inbound\\{string.Join(", Inbound\\", c.Feeds.OrderBy(f => f))})\n" +
            $"Drop account   {(c.DropAccount.Length == 0 ? "(none yet: administrators only)" : c.DropAccount)}\n" +
            $"Database       {c.SqlServerInstance} / {c.DatabaseName}\n" +
            $"NiFi SQL login {c.NiFiSqlLogin} (random password, kept in NiFi only)\n" +
            (c.Feeds.Contains("SalesOrder") ? $"Sales orders   type {c.SalesOrderDefaults.DocumentType}, status {c.SalesOrderDefaults.DocumentStatus}, site '{c.SalesOrderDefaults.Site}'\n" : "") +
            (c.Feeds.Contains("PurchaseOrder") ? $"Purchase ord.  type {c.PurchaseOrderDefaults.DocumentType}, status {c.PurchaseOrderDefaults.DocumentStatus}, site '{c.PurchaseOrderDefaults.Site}'\n" : "") +
            "\nInstall deploys the SQL objects first, then NiFi and its service, then imports and starts the flow. " +
            "If NiFi's part fails, what it created is removed so you can run this again.";
        _btnInstall.Enabled = !_done;
    }

    private void Log(LogEntry entry)
    {
        if (InvokeRequired) { BeginInvoke(new MethodInvoker(() => Log(entry))); return; }
        string line = entry.Level == LogLevel.Stage
            ? $"\n== {entry.Message}\n"
            : $"{entry.Timestamp:HH:mm:ss} {entry.Prefix} {entry.Message}\n";
        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.SelectionColor = entry.Level switch
        {
            LogLevel.Success => Color.SeaGreen,
            LogLevel.Warning => Color.DarkGoldenrod,
            LogLevel.Error => Color.Firebrick,
            LogLevel.Detail => Color.Gray,
            LogLevel.Stage => Color.FromArgb(31, 41, 55),
            _ => Color.Black
        };
        _txtLog.AppendText(line);
        _txtLog.ScrollToCaret();
        try { _logFile?.Write(entry.Level == LogLevel.Stage ? line : $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} {entry.Prefix} {entry.Message}\r\n"); _logFile?.Flush(); }
        catch (IOException) { /* the on-screen log still has it */ }
    }

    private void OpenLogFile()
    {
        try
        {
            SecureFolders.EnsureAdminOnly(MediaStaging.DataRoot);
            SecureFolders.EnsureAdminOnly(MediaStaging.LogFolder);
            _logPath = Path.Combine(MediaStaging.LogFolder, $"NiFiDeploy_{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _logFile = new StreamWriter(_logPath, append: false, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logPath = null;
            Log(new LogEntry(LogLevel.Warning, "No log file this time: " + ex.Message));
        }
    }

    private async Task RunAsync()
    {
        if (_context is null) return;
        var confirm = MessageBox.Show(this,
            $"Install NiFi as service {_context.ServiceName} and deploy the import objects into {_context.DatabaseName} on {_context.SqlServerInstance}?",
            "Install", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK) return;

        _btnInstall.Enabled = false;
        RunningChanged?.Invoke(this, true);
        _txtLog.Clear();
        OpenLogFile();
        _cts = new CancellationTokenSource();
        var runner = new DeployRunner(Log);
        Log(new LogEntry(LogLevel.Info, $"Granite NiFi Deploy {MainForm.VersionLabel} on {Environment.MachineName} by {Environment.UserDomainName}\\{Environment.UserName}"));

        try
        {
            await runner.RunAsync(_context, _cts.Token);
            _done = true;
            Log(new LogEntry(LogLevel.Success, $"NiFi is running at {_context.NiFiUrl} (sign in as {_context.AdminUser})."));
            Log(new LogEntry(LogLevel.Info, $"Drop CSV files into {_context.InboundFolder}\\<Feed>. Results are in dbo.Custom_NiFiImportLog."));
            foreach (string f in runner.FollowUps) Log(new LogEntry(LogLevel.Warning, "To do: " + f));
            _btnOpenNiFi.Visible = _btnOpenInbound.Visible = true;
            MessageBox.Show(this,
                runner.FollowUps.Count == 0 ? "NiFi is installed and the Granite import is running." : "NiFi is installed, with " + runner.FollowUps.Count + " thing(s) to check. See \"To do\" in the log.",
                "Install finished", MessageBoxButtons.OK, runner.FollowUps.Count == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            Log(new LogEntry(LogLevel.Error, ex.Message));
            foreach (string f in runner.FollowUps) Log(new LogEntry(LogLevel.Warning, "To do: " + f));
            MessageBox.Show(this, $"Install stopped: {ex.Message}\n\nSee the log for details.", "Install failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _btnInstall.Enabled = true;
        }
        finally
        {
            // Kept after a failure so Install can be clicked again; gone once it worked.
            if (_done) _context.ForgetSecrets();
            if (_logFile is not null) { await _logFile.DisposeAsync(); _logFile = null; }
            _btnOpenLog.Visible = _logPath is not null;
            RunningChanged?.Invoke(this, false);
        }
    }

    private void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't open", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    public override bool ValidateStep(DeployContext context, out string error)
    {
        error = string.Empty;
        return true;
    }
}
