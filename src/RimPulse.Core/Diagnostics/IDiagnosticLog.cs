namespace RimPulse.Core.Diagnostics;

/// <summary>
/// Diagnostic sink used across RimPulse. Implementations must never throw from <see cref="Write"/>:
/// logging failures may not take the application down.
/// </summary>
public interface IDiagnosticLog
{
    event EventHandler<LogEntry>? EntryWritten;

    void Write(LogLevel level, string category, string message, string? detail = null);
}

public static class DiagnosticLogExtensions
{
    public static void Debug(this IDiagnosticLog log, string category, string message, string? detail = null)
        => log.Write(LogLevel.Debug, category, message, detail);

    public static void Info(this IDiagnosticLog log, string category, string message, string? detail = null)
        => log.Write(LogLevel.Info, category, message, detail);

    public static void Warning(this IDiagnosticLog log, string category, string message, string? detail = null)
        => log.Write(LogLevel.Warning, category, message, detail);

    public static void Error(this IDiagnosticLog log, string category, string message, Exception? exception = null)
        => log.Write(LogLevel.Error, category, message, exception?.ToString());
}
