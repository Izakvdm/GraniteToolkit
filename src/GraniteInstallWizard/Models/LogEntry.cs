namespace GraniteInstallWizard.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,

    /// <summary>
    /// Low-importance detail: SQL Server PRINT output from the database
    /// scripts (GraniteDatabase_Create.sql prints one line per object it
    /// creates, well over a thousand of them) and the exact command lines
    /// of external tools. Rendered grey in the console so the real
    /// progress lines stand out, and still written to the log file.
    /// </summary>
    Detail,

    /// <summary>A dry run's "would do X" line: nothing was changed.</summary>
    DryRun,

    /// <summary>Start of one of the ten install stages.</summary>
    Stage
}

/// <summary>One line rendered into the Step 6 log console and the log file.</summary>
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
}
