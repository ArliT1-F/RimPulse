namespace RimPulse.Core.Diagnostics;

/// <summary>
/// Bounded, thread-safe in-memory log with an optional persistent sink. The in-memory buffer is
/// capped so a long development session cannot grow without limit (see README §17).
/// </summary>
public sealed class DiagnosticLog : IDiagnosticLog, IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<LogEntry> _buffer;
    private readonly int _capacity;
    private readonly IAppendOnlyLogSink? _sink;
    private bool _disposed;

    public DiagnosticLog(int capacity = 1000, LogLevel minimumLevel = LogLevel.Debug, IAppendOnlyLogSink? sink = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The buffer capacity must be at least one.");
        }

        _capacity = capacity;
        _buffer = new Queue<LogEntry>(Math.Min(capacity, 256));
        MinimumLevel = minimumLevel;
        _sink = sink;
    }

    public event EventHandler<LogEntry>? EntryWritten;

    /// <summary>Entries below this level are discarded.</summary>
    public LogLevel MinimumLevel { get; set; }

    public void Write(LogLevel level, string category, string message, string? detail = null)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.UtcNow, level, category ?? "General", message ?? string.Empty, detail);

        EventHandler<LogEntry>? handler;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _buffer.Enqueue(entry);
            while (_buffer.Count > _capacity)
            {
                _buffer.Dequeue();
            }

            handler = EntryWritten;
        }

        if (handler is not null)
        {
            try
            {
                handler(this, entry);
            }
            catch (Exception)
            {
                // A misbehaving subscriber must not break logging for everyone else.
            }
        }

        if (_sink is not null)
        {
            try
            {
                _sink.Append(entry);
            }
            catch (Exception)
            {
                // Disk problems are reported by the UI via other means; logging must stay alive.
            }
        }
    }

    /// <summary>Snapshot of the current buffer, oldest entry first.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _buffer.ToArray();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _buffer.Clear();
        }

        if (_sink is IDisposable disposableSink)
        {
            disposableSink.Dispose();
        }
    }
}
