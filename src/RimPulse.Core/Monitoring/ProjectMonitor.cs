using System.Diagnostics;

namespace RimPulse.Core.Monitoring;

/// <summary>
/// Watches one existing project. File system events are best-effort; errors mean changes may have
/// been missed and callers must tell the user to re-inspect. Events are delivered on worker threads.
/// </summary>
public sealed class ProjectMonitor : IDisposable
{
    private const int MaxPendingPaths = 4096;
    private readonly object _gate = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly Dictionary<string, ProjectChange> _pending;
    private readonly TimeSpan _debounce;
    private bool _disposed;
    private bool _overflowReported;
    private long _lastQueuedAt;

    public ProjectMonitor(string rootPath, TimeSpan? debounce = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        _debounce = debounce ?? TimeSpan.FromMilliseconds(350);
        if (_debounce <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        _pending = new Dictionary<string, ProjectChange>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        _timer = new Timer(Flush, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            _watcher = new FileSystemWatcher(RootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
        }
        catch
        {
            _timer.Dispose();
            throw;
        }
    }

    public string RootPath { get; }
    public event EventHandler<IReadOnlyList<ProjectChange>>? ChangesDetected;
    public event EventHandler<string>? MonitoringError;

    /// <summary>Begins monitoring after subscribers are attached.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _watcher.EnableRaisingEvents = true;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Queue(e.FullPath, e.ChangeType);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // Atomic saves and directory renames can affect both sides, even if one side is ignored.
        Queue(e.OldFullPath, WatcherChangeTypes.Deleted);
        Queue(e.FullPath, WatcherChangeTypes.Created);
    }

    private void Queue(string fullPath, WatcherChangeTypes change)
    {
        var relative = Path.GetRelativePath(RootPath, fullPath);
        var category = ProjectChangeClassifier.Classify(relative);
        if (category is null) return;
        var overflow = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_pending.ContainsKey(relative) && _pending.Count >= MaxPendingPaths)
            {
                if (!_overflowReported)
                {
                    _overflowReported = true;
                    overflow = true;
                }
            }
            else
            {
                if (_pending.TryGetValue(relative, out var previous)) change |= previous.Changes;
                _pending[relative] = new ProjectChange(relative, category.Value, DateTimeOffset.Now, change);
                _lastQueuedAt = Stopwatch.GetTimestamp();
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
        }
        if (overflow) ReportError("Too many pending changes; some events may be missing. Re-inspect the project.");
    }

    private void Flush(object? state)
    {
        ProjectChange[] changes;
        lock (_gate)
        {
            if (_disposed || _pending.Count == 0) return;
            var elapsed = Stopwatch.GetElapsedTime(_lastQueuedAt);
            if (elapsed < _debounce)
            {
                _timer.Change(_debounce - elapsed, Timeout.InfiniteTimeSpan);
                return;
            }
            changes = _pending.Values.OrderBy(c => c.DetectedAt).ToArray();
            _pending.Clear();
            _overflowReported = false;
        }
        ChangesDetected?.Invoke(this, changes);
    }

    private void OnError(object sender, ErrorEventArgs e) =>
        ReportError($"File monitoring error: {e.GetException().Message}. Changes may have been missed; re-inspect the project.");

    private void ReportError(string message) => MonitoringError?.Invoke(this, message);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _watcher.EnableRaisingEvents = false;
            _pending.Clear();
            _timer.Dispose();
            _watcher.Dispose();
        }
    }
}
