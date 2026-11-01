namespace RimPulse.Core.Tests.TestSupport;

/// <summary>
/// A throw-away directory under the system temp path. Tests never touch the real user profile, so they
/// are safe to run on a developer machine that has RimWorld installed.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "rimpulse-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts)
    {
        var segments = new string[parts.Length + 1];
        segments[0] = Root;
        parts.CopyTo(segments, 1);
        return Path.Combine(segments);
    }

    public string CreateDirectory(params string[] parts)
    {
        var directory = Combine(parts);
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Creates a file (and its parent directories) using a '/'-separated relative path.</summary>
    public string CreateFile(string relativePath, string content = "")
    {
        var segments = relativePath.Split('/');
        var filePath = Combine(segments);
        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, content);
        return filePath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked file on Windows should not fail an otherwise passing test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
