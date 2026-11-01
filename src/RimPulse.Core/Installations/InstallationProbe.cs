using RimPulse.Core.Game;

namespace RimPulse.Core.Installations;

/// <summary>
/// Inspects a directory and reports what kind of RimWorld installation (if any) it contains.
/// </summary>
/// <remarks>
/// <para>
/// Two layouts are recognised, and they are genuinely different (see docs/GAME-VERSIONS.md):
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Unity-era (RimWorld 1.6 and earlier)</b>: a Unity player layout where managed assemblies live
///       in <c>RimWorldLinux_Data/Managed</c>, <c>RimWorldWin64_Data/Managed</c> or, for the macOS
///       bundle, <c>Contents/Resources/Data/Managed</c>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>Waylith-era (RimWorld 1.7 and later)</b>: assemblies sit directly in the installation root.
///     </description>
///   </item>
/// </list>
/// <para>
/// The probe never guesses the game version. It only reports what <c>Version.txt</c> says, and reports
/// the version as unconfirmed when that file is missing or unreadable.
/// </para>
/// </remarks>
public static class InstallationProbe
{
    private static readonly string[] UnityManagedRelativePaths =
    [
        Path.Combine("RimWorldLinux_Data", "Managed"),
        Path.Combine("RimWorldWin64_Data", "Managed"),
        Path.Combine("RimWorldMac_Data", "Managed"),
        Path.Combine("RimWorldMac.app", "Contents", "Resources", "Data", "Managed"),
        Path.Combine("Contents", "Resources", "Data", "Managed"),
    ];

    /// <summary>Assemblies the Waylith era installation exposes in its root.</summary>
    public static readonly IReadOnlyList<string> WaylithRequiredAssemblies =
    [
        "Assembly-CSharp.dll",
        "Waylith.dll",
        "UnityStubs.dll",
    ];

    /// <summary>Assemblies the Unity era installation exposes in its Managed directory.</summary>
    public static readonly IReadOnlyList<string> UnityRequiredAssemblies =
    [
        "Assembly-CSharp.dll",
        "UnityEngine.CoreModule.dll",
    ];

    public static RimWorldInstallation Probe(string candidatePath, InstallationSource source = InstallationSource.Manual)
    {
        var (rootPath, managedHint) = ResolveInstallationRoot(candidatePath);

        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            var reportedPath = string.IsNullOrEmpty(rootPath) ? (candidatePath ?? string.Empty) : rootPath;
            return new RimWorldInstallation
            {
                RootPath = reportedPath,
                DisplayName = GetDisplayName(reportedPath, GameVersion.Unknown),
                Layout = InstallationLayout.Unknown,
                Version = GameVersion.Unknown,
                VersionSource = GameVersionSource.Unknown,
                Source = source,
                RootExists = false,
                Problems = [$"The directory '{reportedPath}' does not exist or is not accessible."],
            };
        }

        var versionText = VersionFileReader.ReadVersionText(rootPath);
        var version = GameVersion.TryParse(versionText, out var parsedVersion) ? parsedVersion : GameVersion.Unknown;
        var versionSource = version.IsKnown ? GameVersionSource.VersionFile : GameVersionSource.Unknown;

        var problems = new List<string>();
        var layout = InstallationLayout.Unknown;
        string? managedDirectory = null;
        IReadOnlyList<string> requiredAssemblies = [];

        if (managedHint is not null && Directory.Exists(managedHint))
        {
            layout = InstallationLayout.UnityMono;
            managedDirectory = managedHint;
            requiredAssemblies = UnityRequiredAssemblies;
        }
        else if (File.Exists(Path.Combine(rootPath, "Assembly-CSharp.dll")) || File.Exists(Path.Combine(rootPath, "Waylith.dll")))
        {
            layout = InstallationLayout.Waylith;
            managedDirectory = rootPath;
            requiredAssemblies = WaylithRequiredAssemblies;
        }
        else
        {
            var unityManagedDirectory = FindUnityManagedDirectory(rootPath);
            if (unityManagedDirectory is not null)
            {
                layout = InstallationLayout.UnityMono;
                managedDirectory = unityManagedDirectory;
                requiredAssemblies = UnityRequiredAssemblies;
            }
        }

        if (layout == InstallationLayout.Unknown || managedDirectory is null)
        {
            problems.Add(
                "No RimWorld managed assemblies were found. A RimWorld 1.7+ installation keeps Assembly-CSharp.dll, " +
                "Waylith.dll and UnityStubs.dll in its root; a RimWorld 1.6 installation keeps them in " +
                "RimWorldLinux_Data/Managed (or the Windows/macOS equivalent).");
        }

        var presentAssemblies = new List<string>();
        var missingAssemblies = new List<string>();

        if (managedDirectory is not null)
        {
            foreach (var assembly in requiredAssemblies)
            {
                if (File.Exists(Path.Combine(managedDirectory, assembly)))
                {
                    presentAssemblies.Add(assembly);
                }
                else
                {
                    missingAssemblies.Add(assembly);
                }
            }

            if (missingAssemblies.Count > 0)
            {
                problems.Add($"Missing required assemblies: {string.Join(", ", missingAssemblies)}.");
            }
        }

        if (!version.IsKnown)
        {
            problems.Add(
                versionText is null
                    ? $"'{VersionFileReader.VersionFileName}' was not found in the installation root, so the game version could not be confirmed."
                    : $"'{VersionFileReader.VersionFileName}' contains '{versionText}', which is not a version RimPulse understands.");
        }
        else if (!version.IsSupported)
        {
            problems.Add(
                $"RimWorld {version.ShortVersion} is not a supported target version. Supported versions: 1.6 and 1.7.");
        }

        return new RimWorldInstallation
        {
            RootPath = rootPath,
            DisplayName = GetDisplayName(rootPath, version),
            Layout = layout,
            Version = version,
            VersionSource = versionSource,
            Source = source,
            RootExists = true,
            VersionFileContents = versionText,
            ManagedAssemblyDirectory = managedDirectory,
            PresentAssemblies = presentAssemblies,
            MissingAssemblies = missingAssemblies,
            Problems = problems,
        };
    }

    /// <summary>
    /// Normalises a user- or configuration-supplied path: trims quotes and whitespace, expands a leading
    /// <c>~</c> and makes the path absolute. Returns the trimmed input when it cannot be resolved.
    /// </summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var value = path.Trim().Trim('"');

        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = value.Length == 1 ? home : Path.Combine(home, value[2..]);
        }

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return value;
        }
    }

    private static (string RootPath, string? ManagedDirectoryHint) ResolveInstallationRoot(string candidatePath)
    {
        var normalized = NormalizePath(candidatePath);
        if (normalized.Length == 0)
        {
            return (string.Empty, null);
        }

        // The user pointed directly at a Managed directory; recover the installation root from it.
        if (string.Equals(Path.GetFileName(normalized), "Managed", StringComparison.OrdinalIgnoreCase))
        {
            return (FindInstallationRoot(normalized), Directory.Exists(normalized) ? normalized : null);
        }

        // macOS: the user pointed at the .app bundle.
        var bundleManagedDirectory = Path.Combine(normalized, "Contents", "Resources", "Data", "Managed");
        if (Directory.Exists(bundleManagedDirectory))
        {
            return (normalized, bundleManagedDirectory);
        }

        return (normalized, null);
    }

    private static string FindInstallationRoot(string managedDirectory)
    {
        var probe = new DirectoryInfo(managedDirectory);

        for (var depth = 0; depth < 6; depth++)
        {
            probe = probe.Parent;
            if (probe is null)
            {
                break;
            }

            if (File.Exists(Path.Combine(probe.FullName, VersionFileReader.VersionFileName)))
            {
                return probe.FullName;
            }

            if (probe.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return probe.FullName;
            }
        }

        try
        {
            return Path.GetFullPath(Path.Combine(managedDirectory, "..", ".."));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return managedDirectory;
        }
    }

    private static string? FindUnityManagedDirectory(string rootPath)
    {
        string? firstExisting = null;

        foreach (var relativePath in UnityManagedRelativePaths)
        {
            var candidate = Path.Combine(rootPath, relativePath);
            if (!Directory.Exists(candidate))
            {
                continue;
            }

            if (File.Exists(Path.Combine(candidate, "Assembly-CSharp.dll")))
            {
                return candidate;
            }

            firstExisting ??= candidate;
        }

        return firstExisting;
    }

    private static string GetDisplayName(string rootPath, GameVersion version)
    {
        if (version.IsKnown)
        {
            return $"RimWorld {version.ShortVersion}";
        }

        var folderName = string.IsNullOrEmpty(rootPath) ? null : Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(folderName) ? "RimWorld (version unconfirmed)" : $"RimWorld (version unconfirmed) — {folderName}";
    }
}
