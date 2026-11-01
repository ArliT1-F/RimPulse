namespace RimPulse.Core.Monitoring;

/// <summary>The kind of project content affected, not a claim that it can be reloaded.</summary>
public enum ProjectChangeCategory
{
    CSharp,
    AboutMetadata,
    Language,
    Xml,
    Texture,
    Audio,
    Other,
}

/// <summary>A coalesced change to one project-relative path.</summary>
public sealed record ProjectChange(string RelativePath, ProjectChangeCategory Category,
    DateTimeOffset DetectedAt, WatcherChangeTypes Changes);

/// <summary>Classifies paths using names and extensions without depending on file existence.</summary>
public static class ProjectChangeClassifier
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".vs", ".idea", ".vscode", "bin", "obj" };
    private static readonly HashSet<string> Textures = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".dds", ".tga", ".bmp" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { ".ogg", ".wav", ".mp3" };

    /// <summary>Returns null for temporary files and known non-project directories.</summary>
    public static ProjectChangeCategory? Classify(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
        var parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..") || parts.Any(IgnoredDirectories.Contains)) return null;
        var file = parts[^1];
        if (file.StartsWith(".#", StringComparison.Ordinal) || file.StartsWith("~$", StringComparison.Ordinal)
            || file.EndsWith('~') || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".swp", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".swx", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return null;
        if (parts.Length == 2 && parts[0].Equals("About", StringComparison.OrdinalIgnoreCase)
            && file.Equals("About.xml", StringComparison.OrdinalIgnoreCase)) return ProjectChangeCategory.AboutMetadata;
        if (parts.Any(p => p.Equals("Languages", StringComparison.OrdinalIgnoreCase))) return ProjectChangeCategory.Language;
        var extension = Path.GetExtension(file);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return ProjectChangeCategory.CSharp;
        if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) return ProjectChangeCategory.Xml;
        if (Textures.Contains(extension)) return ProjectChangeCategory.Texture;
        if (Audio.Contains(extension)) return ProjectChangeCategory.Audio;
        return ProjectChangeCategory.Other;
    }
}
