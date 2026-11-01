namespace RimPulse.Core.Diagnostics;

/// <summary>Optional persistent destination for log entries (a rotating file, in practice).</summary>
public interface IAppendOnlyLogSink
{
    void Append(LogEntry entry);
}
