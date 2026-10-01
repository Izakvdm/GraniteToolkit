namespace GraniteBiDeployWizard.Models;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>One line rendered into the Panel 6 log console.</summary>
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
