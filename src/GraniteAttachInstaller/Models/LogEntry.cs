namespace GraniteAttachInstaller.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,

    /// <summary>Low-importance detail: exact command lines run, per-batch SQL progress.</summary>
    Detail,

    /// <summary>Start of one of the install stages.</summary>
    Stage
}

/// <summary>One line rendered into Step 3's log and kept for the summary screen.</summary>
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

    public string Prefix => Level switch
    {
        LogLevel.Success => "[OK]",
        LogLevel.Warning => "[WARN]",
        LogLevel.Error => "[ERROR]",
        LogLevel.Detail => "      ",
        LogLevel.Stage => "==",
        _ => "[INFO]"
    };
}
