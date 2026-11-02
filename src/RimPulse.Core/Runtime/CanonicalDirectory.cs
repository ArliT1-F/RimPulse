namespace RimPulse.Core.Runtime;

/// <summary>Resolves directory symlinks in every path component, not just the final component.</summary>
internal static class CanonicalDirectory
{
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute directory path is required.", nameof(path));
        var remainingLinks = 64;
        return Path.TrimEndingDirectorySeparator(ResolveComponents(Path.GetFullPath(path), ref remainingLinks));
    }

    private static string ResolveComponents(string path, ref int remainingLinks)
    {
        var root = Path.GetPathRoot(path) ?? throw new ArgumentException("A rooted directory is required.", nameof(path));
        var resolved = root;
        var components = path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var component in components)
        {
            var info = new DirectoryInfo(Path.Combine(resolved, component));
            if (!info.Exists) throw new DirectoryNotFoundException($"Directory not found: '{info.FullName}'.");
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                resolved = info.FullName;
                continue;
            }

            if (--remainingLinks < 0) throw new IOException("Too many symbolic links while resolving a directory.");
            resolved = ResolveComponents(Path.GetFullPath(target.FullName), ref remainingLinks);
        }
        return resolved;
    }
}
