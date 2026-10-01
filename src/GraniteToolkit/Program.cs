using System.Reflection;
using Granite.Toolkit.Core.Logging;
using Granite.Toolkit.Core.Security;
using GraniteToolkit.Logic;
using GraniteToolkit.Services;
using GraniteToolkit.UI;

namespace GraniteToolkit;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One toolkit at a time per machine (all sessions), so two operators
        // can't run installers against the same IIS at once.
        using var single = new Mutex(initiallyOwned: true, @"Global\GraniteWMS.Toolkit.Launcher", out bool first);
        if (!first)
        {
            MessageBox.Show("The GraniteWMS Toolkit is already open on this server (possibly in another user's session).",
                "GraniteWMS Toolkit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        var banners = new List<(HealthState, string)>();

        // 1. Our own signature: decides how modules are checked.
        var self = Authenticode.Verify(Environment.ProcessPath ?? Application.ExecutablePath);
        if (self.State == SignatureState.Invalid)
        {
            MessageBox.Show(
                $"The toolkit's own signature doesn't verify: {self.Detail}.\n\nIt may have been changed after it was signed. Reinstall it from a trusted copy.",
                "GraniteWMS Toolkit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (self.State == SignatureState.Unsigned)
            banners.Add((HealthState.Attention, "Development build: the toolkit isn't signed. Use a signed release on client servers."));

        // 2. Where we're running from: an elevated tool in a folder ordinary users can change is a risk.
        var writers = SecureFolders.UntrustedWriters(ToolkitPaths.AppFolder);
        if (writers.Count > 0)
            banners.Add((HealthState.Missing,
                $"Running from a folder that {string.Join(", ", writers)} can change ({ToolkitPaths.AppFolder}). " +
                "Install the toolkit with its MSI, or move it to a folder only administrators can change."));

        // 3. Our data folder (logs): admin-only, or no file log.
        var (log, problem) = LauncherLog.Open();
        if (problem is not null)
            banners.Add((HealthState.Attention, $"Log folder not used: {problem}"));

        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        log.Write(new LogEntry(LogLevel.Stage, $"GraniteWMS Toolkit v{version} started by {Environment.UserDomainName}\\{Environment.UserName} from {ToolkitPaths.AppFolder}"));
        log.Write(new LogEntry(LogLevel.Info, $"Signature: {self.State} ({self.Detail}){(self.SignerSubject is null ? "" : " - " + self.SignerSubject)}"));
        if (writers.Count > 0) log.Write(new LogEntry(LogLevel.Warning, $"App folder writable by: {string.Join(", ", writers)}"));

        Application.Run(new MainForm(self, log, banners));
    }
}
