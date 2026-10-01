using System.Text;
using Granite.Toolkit.Core.Logging;
using Granite.Toolkit.Core.Security;

namespace GraniteToolkit.Services;

/// <summary>
/// Appends the launcher's log to C:\ProgramData\Granite Toolkit\Logs, one
/// file per day. It records what was checked and started, never passwords
/// or connection strings (the launcher doesn't handle any). If the folder
/// can't be secured the launcher keeps working without a file log and
/// says so on screen.
/// </summary>
public sealed class LauncherLog
{
    private readonly object _gate = new();
    private readonly string? _file;

    public LauncherLog(string? folder)
    {
        _file = folder is null ? null : Path.Combine(folder, $"launcher-{DateTime.Now:yyyyMMdd}.log");
    }

    public string? FilePath => _file;

    /// <summary>Secures the data and log folders and returns the log, or a screen-only log and the reason.</summary>
    public static (LauncherLog Log, string? Problem) Open()
    {
        try
        {
            SecureFolders.EnsureAdminOnly(ToolkitPaths.DataRoot);
            SecureFolders.EnsureAdminOnly(ToolkitPaths.Logs);
            return (new LauncherLog(ToolkitPaths.Logs), null);
        }
        catch (Exception ex)
        {
            return (new LauncherLog(null), ex.Message);
        }
    }

    public void Write(LogEntry entry)
    {
        if (_file is null) return;
        try
        {
            string line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} {entry.Prefix} {entry.Message}{Environment.NewLine}";
            lock (_gate) File.AppendAllText(_file, line, Encoding.UTF8);
        }
        catch
        {
            // Logging must never stop the tool.
        }
    }
}
