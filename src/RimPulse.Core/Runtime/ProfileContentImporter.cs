using RimPulse.Core.Configuration;

namespace RimPulse.Core.Runtime;

/// <summary>
/// Copies a user-selected game save or scenario into an isolated profile. This does not interpret,
/// modify, load, or verify compatibility of the file; RimWorld remains responsible for doing so.
/// </summary>
public static class ProfileContentImporter
{
    public static string Import(AppPaths paths, DevelopmentProfileSettings profile, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        var extension = Path.GetExtension(source);
        var folder = extension.ToLowerInvariant() switch
        {
            ".rws" => "Saves",
            ".rsc" => "Scenarios",
            _ => throw new ArgumentException("Choose a RimWorld save (.rws) or scenario (.rsc).", nameof(sourcePath)),
        };
        if (!File.Exists(source)) throw new FileNotFoundException("The selected file does not exist or cannot be read.", source);
        var basename = Path.GetFileNameWithoutExtension(source);
        if (string.IsNullOrWhiteSpace(basename) || basename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The selected file has an invalid name.", nameof(sourcePath));

        var data = PersistentDevelopmentProfile.DataDirectory(paths, profile);
        var destinationDirectory = Path.Combine(data, folder);
        RefuseLinkedDirectories(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        RefuseLinkedDirectories(destinationDirectory);
        // RimWorld 1.6 discovers lowercase extensions on case-sensitive filesystems.
        var destination = Path.Combine(destinationDirectory, basename + extension.ToLowerInvariant());
        var temporary = Path.Combine(destinationDirectory, ".rimpulse-import-" + Guid.NewGuid().ToString("N") + ".tmp");
        var createdTemporary = false;
        try
        {
            // Copy to a private temporary name, then move without overwrite. A failed read or collision
            // never leaves a partially written game file or replaces an existing save/scenario.
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdTemporary = true;
                input.CopyTo(output);
            }
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        finally
        {
            if (createdTemporary && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RefuseLinkedDirectories(string path)
    {
        for (var info = new DirectoryInfo(path); info is not null && info.FullName != Path.GetPathRoot(info.FullName); info = info.Parent)
            if (info.LinkTarget is not null)
                throw new IOException($"Refusing to import through profile symlink '{info.FullName}'.");
    }
}
