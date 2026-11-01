using RimPulse.Core.Platform;

namespace RimPulse.Core.Installations.Steam;

/// <summary>
/// Finds RimWorld inside Steam libraries. Steam is by far the most common way RimWorld is installed on
/// Linux (native build in <c>steamapps/common/RimWorld</c>), so this is the primary auto-discovery path.
/// </summary>
public static class SteamLibraryReader
{
    /// <summary>RimWorld's Steam application id.</summary>
    public const int RimWorldAppId = 294100;

    private const string DefaultInstallFolderName = "RimWorld";

    /// <summary>
    /// Candidate Steam roots for the platform. Missing directories are harmless: callers simply find no
    /// library file and move on.
    /// </summary>
    public static IReadOnlyList<string> GetSteamRootCandidates(string homeDirectory, HostPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return [];
        }

        var candidates = new List<string>();

        switch (platform)
        {
            case HostPlatform.Windows:
                candidates.Add(@"C:\Program Files (x86)\Steam");
                candidates.Add(@"C:\Program Files\Steam");
                candidates.Add(Path.Combine(homeDirectory, "AppData", "Local", "Steam"));
                break;

            case HostPlatform.MacOS:
                candidates.Add(Path.Combine(homeDirectory, "Library", "Application Support", "Steam"));
                break;

            default:
                // Native, Debian-package and Flatpak Steam layouts seen on Linux Mint / Ubuntu.
                candidates.Add(Path.Combine(homeDirectory, ".steam", "steam"));
                candidates.Add(Path.Combine(homeDirectory, ".steam", "root"));
                candidates.Add(Path.Combine(homeDirectory, ".steam", "debian-installation"));
                candidates.Add(Path.Combine(homeDirectory, ".local", "share", "Steam"));
                candidates.Add(Path.Combine(homeDirectory, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
                break;
        }

        return candidates;
    }

    /// <summary>
    /// Reads <c>steamapps/libraryfolders.vdf</c> and returns every library folder. Both the modern
    /// (nested <c>"path"</c>) and the legacy (index → path) formats are supported.
    /// </summary>
    /// <exception cref="ValveKeyValuesFormatException">The file exists but is malformed.</exception>
    public static IReadOnlyList<string> ReadLibraryFolders(string steamRoot)
    {
        var libraries = new List<string>();

        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            return libraries;
        }

        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");

        if (File.Exists(vdfPath))
        {
            var document = ValveKeyValues.Parse(File.ReadAllText(vdfPath));
            var section = document.GetSection("libraryfolders") ?? document.GetSection("LibraryFolders");
            var entries = section is not null ? section.Children : document.Children;

            foreach (var entry in entries)
            {
                if (entry.HasChildren)
                {
                    var path = entry.GetValue("path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        libraries.Add(path.Trim());
                    }

                    continue;
                }

                // Legacy format: "1"  "/home/user/Games/SteamLibrary"
                var value = entry.Value?.Trim();
                if (!string.IsNullOrEmpty(value) && int.TryParse(entry.Key, out _) && Path.IsPathRooted(value))
                {
                    libraries.Add(value);
                }
            }
        }

        if (libraries.Count == 0 && Directory.Exists(steamRoot))
        {
            libraries.Add(steamRoot);
        }

        return Deduplicate(libraries);
    }

    /// <summary>
    /// Reads <c>appmanifest_&lt;appId&gt;.acf</c> from a library and returns the absolute install directory,
    /// or <c>null</c> when the app is not installed there.
    /// </summary>
    public static string? ReadInstalledDirectory(string libraryRoot, int appId)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return null;
        }

        var manifestPath = Path.Combine(libraryRoot, "steamapps", $"appmanifest_{appId}.acf");

        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var document = ValveKeyValues.Parse(File.ReadAllText(manifestPath));
        var state = document.GetSection("AppState") ?? document;
        var installDirectory = state.GetValue("installdir");

        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        return Path.Combine(libraryRoot, "steamapps", "common", installDirectory.Trim());
    }

    /// <summary>
    /// All plausible RimWorld directories inside one Steam library: the manifest's <c>installdir</c> when
    /// present, plus the conventional folder name as a fallback for libraries without a manifest.
    /// </summary>
    public static IReadOnlyList<string> FindRimWorldInstallRoots(string libraryRoot)
    {
        var roots = new List<string>();

        if (string.IsNullOrWhiteSpace(libraryRoot))
        {
            return roots;
        }

        var fromManifest = ReadInstalledDirectory(libraryRoot, RimWorldAppId);
        if (!string.IsNullOrWhiteSpace(fromManifest))
        {
            roots.Add(fromManifest);
        }

        var conventional = Path.Combine(libraryRoot, "steamapps", "common", DefaultInstallFolderName);
        if (Directory.Exists(conventional))
        {
            roots.Add(conventional);
        }

        return Deduplicate(roots);
    }

    private static List<string> Deduplicate(List<string> paths)
    {
        var result = new List<string>(paths.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var trimmed = path.Trim();
            if (seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
