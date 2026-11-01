using RimPulse.Core.Diagnostics;

namespace RimPulse.Core.Tests.TestSupport;

internal sealed class RecordingLogSink : IAppendOnlyLogSink
{
    public List<LogEntry> Entries { get; } = [];

    public void Append(LogEntry entry) => Entries.Add(entry);
}

internal sealed class ThrowingLogSink : IAppendOnlyLogSink
{
    public void Append(LogEntry entry) => throw new IOException("disk is on fire");
}
