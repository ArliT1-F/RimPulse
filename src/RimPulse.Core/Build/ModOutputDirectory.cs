using RimPulse.Core.Game;
using RimPulse.Core.Installations;

namespace RimPulse.Core.Build;

/// <summary>
/// The folder a build writes its assemblies into: either the mod's own <c>Assemblies</c> folder or the
/// <c>Assemblies</c> folder of a version-named one, e.g. <c>MyMod/1.6/Assemblies</c>.
/// </summary>
/// <param name="Path">Absolute path of the folder the DLLs are written to.</param>
/// <param name="VersionFolder">
/// Name of the version folder the output sits in (<c>1.6</c>), or null when the output is the mod root's own
/// <c>Assemblies</c> folder.
/// </param>
public sealed record ModOutputDirectory(string Path, string? VersionFolder)
{
    /// <summary>The folder name RimWorld loads a mod's DLLs from, in both layouts.</summary>
    public const string AssembliesFolderName = "Assemblies";

    /// <summary>True when the assemblies land below a version-named folder instead of the mod root.</summary>
    public bool IsVersioned => VersionFolder is not null;

    /// <summary>How the chosen folder is described to the user.</summary>
    public string Description => IsVersioned
        ? $"the mod's {VersionFolder}/{AssembliesFolderName} folder"
        : $"the mod's {AssembliesFolderName} folder";
}

/// <summary>
/// Decides where a build writes its assemblies.
/// </summary>
/// <remarks>
/// RimWorld loads a mod's DLLs from <c>Assemblies</c>, and a mod that splits its content by version keeps that
/// folder inside the version's own root: for 1.6 the game reads <c>MyMod/1.6/Assemblies</c>, because
/// <c>MyMod/1.6</c> is the mod root for that version. In that layout a DLL written to the top-level
/// <c>MyMod/Assemblies</c> is not the assembly the 1.6 game loads, so the version folder wins whenever the
/// selected installation's <c>major.minor</c> folder is present. A mod without version folders keeps the
/// single top-level <c>Assemblies</c> folder.
/// </remarks>
public static class ModOutputDirectoryResolver
{
    /// <summary>
    /// Resolves the output folder for a mod built against <paramref name="version"/>.
    /// </summary>
    /// <param name="modRootPath">The inspected mod directory (the folder that holds <c>About</c>).</param>
    /// <param name="version">The selected installation's version, which decides the version folder.</param>
    public static ModOutputDirectory Resolve(string modRootPath, GameVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modRootPath);

        var root = InstallationProbe.NormalizePath(modRootPath);
        if (root.Length == 0)
        {
            throw new ArgumentException("A mod directory is required.", nameof(modRootPath));
        }

        if (version.IsKnown && FindVersionFolder(root, version.ShortVersion) is { } versionFolder)
        {
            return new(
                Path.Combine(root, versionFolder, ModOutputDirectory.AssembliesFolderName),
                versionFolder);
        }

        return new(Path.Combine(root, ModOutputDirectory.AssembliesFolderName), null);
    }

    /// <summary>
    /// Finds the mod's version folder for <paramref name="shortVersion"/> (<c>1.6</c>), matching the directory
    /// name case-insensitively because the filesystem RimPulse runs on may be one that treats case differently
    /// from the one the mod was authored on.
    /// </summary>
    private static string? FindVersionFolder(string root, string shortVersion)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            string? match = null;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);

                if (string.Equals(name, shortVersion, StringComparison.Ordinal))
                {
                    // An exact match is the folder the game will use, so it wins over a case variant.
                    return name;
                }

                if (match is null && string.Equals(name, shortVersion, StringComparison.OrdinalIgnoreCase))
                {
                    match = name;
                }
            }

            return match;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // An unreadable mod directory is reported by the inspector; here the root folder is still a
            // usable answer, and the write itself will report a permission failure if there is one.
            return null;
        }
    }
}
