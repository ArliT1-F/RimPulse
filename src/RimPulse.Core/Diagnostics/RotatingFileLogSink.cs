using System.Text;

namespace RimPulse.Core.Diagnostics;

/// <summary>
/// Appends log entries to a file and rotates it once it grows past <c>maxBytes</c>, keeping a bounded
/// number of archives. Used so troubleshooting information survives a crash without unbounded disk use.
/// </summary>
public sealed class RotatingFileLogSink : IAppendOnlyLogSink, IDisposable
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly long _maxBytes;
    private readonly int _maxArchives;
    private readonly Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private StreamWriter? _writer;

    public RotatingFileLogSink(string filePath, long maxBytes = 2 * 1024 * 1024, int maxArchives = 3)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A log file path is required.", nameof(filePath));
        }

        if (maxBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "The log file size limit must be at least 1 KiB.");
        }

        if (maxArchives < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxArchives), maxArchives, "At least one archived log file must be kept.");
        }

        _filePath = filePath;
        _maxBytes = maxBytes;
        _maxArchives = maxArchives;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string FilePath => _filePath;

    public void Append(LogEntry entry)
    {
        lock (_gate)
        {
            _writer ??= new StreamWriter(
                new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                _encoding)
            {
                AutoFlush = false,
            };

            _writer.WriteLine(Format(entry));
            _writer.Flush();

            if (_writer.BaseStream.Length >= _maxBytes)
            {
                Rotate();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static string Format(LogEntry entry)
    {
        var builder = new StringBuilder();
        builder.Append(entry.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        builder.Append(" [").Append(entry.Level).Append("] ");
        builder.Append(entry.Category).Append(": ").Append(entry.Message);

        if (!string.IsNullOrEmpty(entry.Detail))
        {
            builder.AppendLine();
            foreach (var line in entry.Detail.Split('\n'))
            {
                builder.Append("    ").Append(line.TrimEnd('\r')).AppendLine();
            }
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        for (var index = _maxArchives; index >= 1; index--)
        {
            var archive = $"{_filePath}.{index}";
            if (!File.Exists(archive))
            {
                continue;
            }

            if (index == _maxArchives)
            {
                File.Delete(archive);
                continue;
            }

            File.Move(archive, $"{_filePath}.{index + 1}", overwrite: true);
        }

        if (File.Exists(_filePath))
        {
            File.Move(_filePath, $"{_filePath}.1", overwrite: true);
        }
    }
}
