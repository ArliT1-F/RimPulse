using RimPulse.Core.Diagnostics;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class DiagnosticLogTests
{
    [Fact]
    public void Write_AppendsEntriesInOrder()
    {
        using var log = new DiagnosticLog(capacity: 10);

        log.Info("Detection", "first");
        log.Error("Detection", "second");

        var entries = log.Snapshot();

        Assert.Equal(2, entries.Count);
        Assert.Equal("first", entries[0].Message);
        Assert.Equal(LogLevel.Error, entries[1].Level);
    }

    [Fact]
    public void Write_TrimsBufferToCapacity()
    {
        using var log = new DiagnosticLog(capacity: 3);

        for (var i = 0; i < 10; i++)
        {
            log.Info("Test", $"entry {i}");
        }

        var entries = log.Snapshot();

        Assert.Equal(3, entries.Count);
        Assert.Equal("entry 7", entries[0].Message);
        Assert.Equal("entry 9", entries[2].Message);
    }

    [Fact]
    public void Write_RespectsMinimumLevel()
    {
        using var log = new DiagnosticLog(capacity: 10, minimumLevel: LogLevel.Warning);

        log.Debug("Test", "hidden");
        log.Info("Test", "hidden too");
        log.Warning("Test", "shown");

        var entries = log.Snapshot();

        Assert.Single(entries);
        Assert.Equal(LogLevel.Warning, entries[0].Level);
    }

    [Fact]
    public void Write_RaisesEntryWritten()
    {
        using var log = new DiagnosticLog(capacity: 10);
        LogEntry? captured = null;
        log.EntryWritten += (_, entry) => captured = entry;

        log.Info("Detection", "hello");

        Assert.NotNull(captured);
        Assert.Equal("hello", captured!.Message);
        Assert.Equal("Detection", captured.Category);
    }

    [Fact]
    public void Write_ForwardsEntriesToSink()
    {
        var sink = new RecordingLogSink();
        using var log = new DiagnosticLog(capacity: 10, sink: sink);

        log.Info("Detection", "persisted");

        Assert.Single(sink.Entries);
        Assert.Equal("persisted", sink.Entries[0].Message);
    }

    [Fact]
    public void Write_SwallowsSinkFailures()
    {
        using var log = new DiagnosticLog(capacity: 10, sink: new ThrowingLogSink());

        var exception = Record.Exception(() => log.Info("Detection", "still safe"));

        Assert.Null(exception);
        Assert.Single(log.Snapshot());
    }

    [Fact]
    public void Write_SwallowsSubscriberFailures()
    {
        using var log = new DiagnosticLog(capacity: 10);
        log.EntryWritten += (_, _) => throw new InvalidOperationException("bad subscriber");

        var exception = Record.Exception(() => log.Info("Detection", "safe"));

        Assert.Null(exception);
    }

    [Fact]
    public void ExtensionMethods_SetTheExpectedLevels()
    {
        using var log = new DiagnosticLog(capacity: 10);

        log.Debug("T", "d");
        log.Info("T", "i");
        log.Warning("T", "w");
        log.Error("T", "e", new InvalidOperationException("boom"));

        var entries = log.Snapshot();

        Assert.Equal(LogLevel.Debug, entries[0].Level);
        Assert.Equal(LogLevel.Info, entries[1].Level);
        Assert.Equal(LogLevel.Warning, entries[2].Level);
        Assert.Equal(LogLevel.Error, entries[3].Level);
        Assert.Contains("boom", entries[3].Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void RotatingFileLogSink_WritesAndRotates()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("logs", "rimpulse.log");

        using (var sink = new RotatingFileLogSink(path, maxBytes: 1024, maxArchives: 2))
        {
            for (var i = 0; i < 200; i++)
            {
                sink.Append(new LogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Info,
                    "Test",
                    new string('x', 100)));
            }
        }

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.False(File.Exists(path + ".3"));

        var currentLength = new FileInfo(path).Length;
        Assert.True(currentLength < 4096, $"Log file should stay bounded, but was {currentLength} bytes.");
    }

    [Fact]
    public void NullDiagnosticLog_AcceptsEverything()
    {
        var log = NullDiagnosticLog.Instance;

        var exception = Record.Exception(() =>
        {
            log.Debug("T", "message");
            log.Info("T", "message");
            log.Error("T", "message", new InvalidOperationException("ignored"));
        });

        Assert.Null(exception);
    }
}
