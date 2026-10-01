namespace GraniteDbSwitcher.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,

    /// <summary>Command lines and other low-importance detail, shown grey.</summary>
    Detail,

    /// <summary>Start of a switch, shown as a heading line.</summary>
    Stage
}

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
