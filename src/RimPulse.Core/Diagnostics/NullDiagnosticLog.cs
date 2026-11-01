namespace RimPulse.Core.Diagnostics;

/// <summary>A log that discards everything. Useful as a constructor default and in tests.</summary>
public sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static readonly NullDiagnosticLog Instance = new();

    public event EventHandler<LogEntry>? EntryWritten
    {
        add { }
        remove { }
    }

    public void Write(LogLevel level, string category, string message, string? detail = null)
    {
    }
}
