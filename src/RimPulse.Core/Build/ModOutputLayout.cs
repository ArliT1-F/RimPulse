using RimPulse.Core.Game;
using RimPulse.Core.Projects;

namespace RimPulse.Core.Build;

/// <summary>
/// Where one mod's assemblies must be written for the selected game version to load them, together with the
/// reason and anything the author has to know about the folder that was chosen.
/// </summary>
/// <param name="Directory">Absolute path of the <c>Assemblies</c> folder the build writes into.</param>
/// <param name="Basis">One sentence naming the evidence for this choice, for the UI and the log.</param>
/// <param name="Notes">Informational lines: nothing is wrong, but the location is not the obvious one.</param>
/// <param name="Problems">Lines that mean the build output may not be loaded, or that a stale copy exists.</param>
public sealed record ModOutputLocation(
    string Directory,
    string Basis,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Problems);

/// <summary>
/// Chooses the folder a build writes into.
/// </summary>
/// <remarks>
/// <para>
/// A mod's assemblies are only loaded when they sit in the <c>Assemblies</c> folder of a folder RimWorld loads
/// for the running game version. <c>&lt;mod root&gt;/Assemblies</c> is correct for a mod with the flat layout,
/// and it is what RimPulse used unconditionally before; for a mod that ships version folders
/// (<c>1.6/Defs</c>, <c>1.6/Assemblies</c>, …) and says so in <c>LoadFolders.xml</c>, the root is not loaded at
/// all, so a DLL written there is silently ignored by the game.
/// </para>
/// <para>
/// The choice is made from the same <see cref="LoadFoldersDocument"/> the project view already shows, so the
/// build target and the "content folders" line can never disagree. Nothing is guessed: when the load folders
/// cannot be determined for the selected version, the root is used (the previous behavior) and the uncertainty
/// is reported instead of being hidden.
/// </para>
/// </remarks>
public static class ModOutputLayout
{
    /// <summary>The folder inside a loaded mod folder that RimWorld reads assemblies from.</summary>
    public const string AssembliesFolderName = "Assemblies";

    /// <summary>
    /// Resolves the output folder for <paramref name="modRootPath"/> on <paramref name="version"/>.
    /// <paramref name="packageId"/> is the mod's own package ID; only it is treated as an active mod, because
    /// RimPulse does not read the game's active mod list (see <c>LoadFoldersReader.Evaluate</c>).
    /// </summary>
    public static ModOutputLocation Resolve(
        string modRootPath,
        LoadFoldersDocument loadFolders,
        GameVersion version,
        string? packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modRootPath);
        ArgumentNullException.ThrowIfNull(loadFolders);

        string root;
        try
        {
            root = Path.GetFullPath(modRootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Unresolved(modRootPath, $"The mod directory could not be resolved: {ex.Message}");
        }

        var rootAssemblies = Path.Combine(root, AssembliesFolderName);

        // No LoadFolders.xml: RimWorld's default rules load the mod root, which is the flat layout.
        if (!loadFolders.Exists)
        {
            return new ModOutputLocation(
                rootAssemblies,
                $"The mod has no {LoadFoldersReader.FileName}, so RimWorld loads the mod root and its " +
                $"{AssembliesFolderName} folder.",
                [],
                []);
        }

        if (!version.IsKnown)
        {
            return new ModOutputLocation(
                rootAssemblies,
                $"The installation has no confirmed game version, so the mod root's {AssembliesFolderName} " +
                "folder is used.",
                [],
                [$"The selected installation has no confirmed version, so {LoadFoldersReader.FileName} could " +
                 $"not be evaluated and the assemblies are written to {rootAssemblies}. Verify in game that " +
                 "this folder is loaded."]);
        }

        var plan = LoadFoldersReader.Evaluate(loadFolders, version, packageId is { Length: > 0 } ? [packageId] : []);

        if (plan.Folders.Count == 0)
        {
            IReadOnlyList<string> problems = plan.Problems.Count > 0
                ? plan.Problems
                : [$"{LoadFoldersReader.FileName} lists no folder for {version.ShortVersion}."];

            return new ModOutputLocation(
                rootAssemblies,
                $"{LoadFoldersReader.FileName} names no folder RimPulse can load for {version.ShortVersion}, so " +
                $"the mod root's {AssembliesFolderName} folder is used.",
                [],
                [.. problems,
                 $"Because the load folders are unknown, RimPulse cannot confirm that {rootAssemblies} is " +
                 "loaded by the game. Verify the folder in game, or add a " +
                 $"<v{version.ShortVersion}> section to {LoadFoldersReader.FileName}."]);
        }

        // The root is loaded, so the flat output folder is loaded too.
        if (plan.Folders.Contains("/", StringComparer.Ordinal))
        {
            return new ModOutputLocation(
                rootAssemblies,
                $"{LoadFoldersReader.FileName} loads the mod root for {version.ShortVersion}, so its " +
                $"{AssembliesFolderName} folder is loaded.",
                [],
                ShadowedAssemblies(root, plan.Folders, target: "/", version));
        }

        var target = ChooseFolder(root, plan.Folders, version);
        if (target is null)
        {
            return new ModOutputLocation(
                rootAssemblies,
                $"{LoadFoldersReader.FileName} loads {string.Join(", ", plan.Folders)} for " +
                $"{version.ShortVersion}, but none of those folders exists, so the mod root's " +
                $"{AssembliesFolderName} folder is used.",
                [],
                [$"{LoadFoldersReader.FileName} loads {string.Join(", ", plan.Folders.Select(FolderDisplay))} " +
                 $"for {version.ShortVersion}, and none of them exists in {root}. RimPulse wrote to " +
                 $"{rootAssemblies} instead, which the game does not load for this version — the mod will " +
                 "appear to have no code. Create the folder the load folders name, or fix the " +
                 $"{LoadFoldersReader.FileName} entry."]);
        }

        var targetDirectory = Path.Combine(root, target.Replace('/', Path.DirectorySeparatorChar), AssembliesFolderName);
        var notes = new List<string>();

        if (!Directory.Exists(targetDirectory))
        {
            notes.Add($"{FolderDisplay(target)}/{AssembliesFolderName} does not exist yet; the build creates it.");
        }

        return new ModOutputLocation(
            targetDirectory,
            $"{LoadFoldersReader.FileName} loads {string.Join(", ", plan.Folders.Select(FolderDisplay))} for " +
            $"{version.ShortVersion} and not the mod root, so assemblies are written to " +
            $"{FolderDisplay(target)}/{AssembliesFolderName}.",
            notes,
            ShadowedAssemblies(root, plan.Folders, target, version));
    }

    /// <summary>
    /// Picks the loaded folder to build into: one that already has an <c>Assemblies</c> folder (that is where
    /// the mod's code lives today), else the folder named after the version, else the first loaded folder.
    /// Returns null when none of the loaded folders exists on disk.
    /// </summary>
    private static string? ChooseFolder(string root, IReadOnlyList<string> folders, GameVersion version)
    {
        var existing = folders
            .Where(folder => folder != "/" && Directory.Exists(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

        if (existing.Length == 0)
        {
            return null;
        }

        var withAssemblies = existing.FirstOrDefault(folder => Directory.Exists(
            Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar), AssembliesFolderName)));

        return withAssemblies
               ?? existing.FirstOrDefault(folder => string.Equals(folder, version.ShortVersion, StringComparison.OrdinalIgnoreCase))
               ?? existing[0];
    }

    /// <summary>
    /// Reports assemblies that the game would load from somewhere the build does not write to: a stale copy in
    /// the mod root when the root is not loaded, and a second copy in another loaded folder.
    /// </summary>
    private static IReadOnlyList<string> ShadowedAssemblies(
        string root, IReadOnlyList<string> loadedFolders, string target, GameVersion version)
    {
        var problems = new List<string>();

        if (!loadedFolders.Contains("/", StringComparer.Ordinal))
        {
            var stale = ListAssemblies(Path.Combine(root, AssembliesFolderName));
            if (stale.Count > 0)
            {
                problems.Add(
                    $"The mod root's {AssembliesFolderName} folder is not loaded for {version.ShortVersion}, so " +
                    $"the assemblies left there are ignored by the game: {string.Join(", ", stale)}. RimPulse now " +
                    $"writes to {TargetDisplay(root, target)}; delete the stale copy so the two cannot be confused.");
            }
        }

        foreach (var folder in loadedFolders)
        {
            // The target folder is where this build writes, and a root that is not loaded was covered above.
            if (string.Equals(folder, target, StringComparison.Ordinal)
                || (folder == "/" && !loadedFolders.Contains("/", StringComparer.Ordinal)))
            {
                continue;
            }

            var directory = folder == "/"
                ? Path.Combine(root, AssembliesFolderName)
                : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar), AssembliesFolderName);

            var assemblies = ListAssemblies(directory);

            if (assemblies.Count > 0)
            {
                problems.Add(
                    $"RimWorld also loads {TargetDisplay(root, folder)}, which already contains " +
                    $"{string.Join(", ", assemblies)}. Two loaded folders holding the same assembly name make " +
                    "the game load the wrong copy; keep each assembly in exactly one of them.");
            }
        }

        return problems;
    }

    private static IReadOnlyList<string> ListAssemblies(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.dll")
                    .Select(path => Path.GetFileName(path))
                    .OfType<string>()
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string FolderDisplay(string folder) => folder == "/" ? "the mod root" : folder;

    private static string TargetDisplay(string root, string folder)
        => folder == "/"
            ? Path.Combine(root, AssembliesFolderName)
            : Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar), AssembliesFolderName);

    private static ModOutputLocation Unresolved(string directory, string problem)
        => new(directory, "The output folder could not be resolved.", [], [problem]);
}
