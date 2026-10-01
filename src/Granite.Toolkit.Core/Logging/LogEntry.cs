namespace Granite.Toolkit.Core.Logging;

/// <summary>
/// Log levels for every toolkit module. The union of what the four wizards
/// had before the toolkit: BI Deploy used the first four only, DB Switcher
/// and Attach added Detail and Stage, and the Install Wizard added DryRun.
/// </summary>
public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,

    /// <summary>
    /// Low-importance detail: SQL Server PRINT output from database scripts,
    /// per-batch progress, and the exact command lines of external tools.
    /// Rendered grey in the log consoles and still written to log files.
    /// </summary>
    Detail,

    /// <summary>A dry run's "would do X" line: nothing was changed.</summary>
    DryRun,

    /// <summary>Start of an install or switch stage, shown as a heading line.</summary>
    Stage
}

/// <summary>One line rendered into a wizard's log console and log file.</summary>
public sealed class LogEntry
{
    public LogEntry(LogLevel level, string message)
    {
        Level = level;
        Message = message;
        Timestamp = DateTime.Now;
    }

    public LogLevel Level { get; }
    public string Message { get; }
    public DateTime Timestamp { get; }

    /// <summary>Plain-text prefix for log files and summary screens.</summary>
    public string Prefix => Level switch
    {
        LogLevel.Success => "[OK]",
        LogLevel.Warning => "[WARN]",
        LogLevel.Error => "[ERROR]",
        LogLevel.Detail => "      ",
        LogLevel.DryRun => "[DRY RUN]",
        LogLevel.Stage => "==",
        _ => "[INFO]"
    };
}
