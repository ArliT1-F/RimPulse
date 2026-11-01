namespace RimPulse.Core.Diagnostics;

/// <summary>A single diagnostic log record.</summary>
public sealed record LogEntry(DateTimeOffset TimestampUtc, LogLevel Level, string Category, string Message, string? Detail = null)
{
    public string TimestampDisplay => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");

    public string LevelLabel => Level.ToShortLabel();
}
