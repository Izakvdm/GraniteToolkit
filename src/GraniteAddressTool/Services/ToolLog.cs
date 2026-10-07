using System.Text;

namespace GraniteAddressTool.Services;

/// <summary>
/// Appends to C:\ProgramData\Granite Toolkit\Logs\address-yyyyMMdd.log
/// (admin-only, see SecureFolders). Records what was changed, never file
/// contents, so connection strings and passwords in appsettings.json never
/// reach the log. If the folder can't be secured, there's no file log and
/// the screen says so.
/// </summary>
public sealed class ToolLog
{
    private readonly string? _file;
    public string? Problem { get; }
    public string? FilePath => _file;

    public ToolLog()
    {
        try
        {
            SecureFolders.EnsureAdminOnly(ToolkitPaths.DataRoot);
            SecureFolders.EnsureAdminOnly(ToolkitPaths.Logs);
            _file = Path.Combine(ToolkitPaths.Logs, $"address-{DateTime.Now:yyyyMMdd}.log");
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
        }
    }

    public void Write(LogEntry entry)
    {
        if (_file is null) return;
        try
        {
            File.AppendAllText(_file, $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss} {entry.Prefix} {entry.Message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* the log never stops the change */ }
    }
}
