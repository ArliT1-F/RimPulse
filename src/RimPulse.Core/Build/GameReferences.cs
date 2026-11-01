using RimPulse.Core.Installations;

namespace RimPulse.Core.Build;

/// <summary>The assembly paths a mod is compiled against, plus the reason any are missing.</summary>
public sealed record GameReferenceSet(IReadOnlyList<string> AssemblyPaths, IReadOnlyList<string> Problems)
{
    public bool IsEmpty => AssemblyPaths.Count == 0;
}

/// <summary>
/// Resolves the assemblies a mod compiles against. The installation is always the first source; for the
/// Waylith layout (RimWorld 1.7, .NET 10) the runtime RimPulse itself runs on supplies the framework
/// assemblies the game expects a mod to reference.
/// </summary>
/// <remarks>
/// This resolves <b>reference</b> assemblies for compilation only. It never loads, patches or executes game
/// code, and a missing assembly is reported instead of being replaced with a stub.
/// </remarks>
public static class GameReferenceResolver
{
    /// <summary>
    /// Resolves the reference set for an installation. <paramref name="installation"/> may be null, which is
    /// reported rather than thrown: compiling without the game is impossible, not exceptional.
    /// </summary>
    public static GameReferenceSet Resolve(RimWorldInstallation? installation, bool includeHostRuntimeAssemblies = true)
    {
        var problems = new List<string>();
        var assemblyPaths = new List<string>();

        if (installation is null)
        {
            problems.Add("No RimWorld installation is selected. Compiling a mod requires the game assemblies.");
            return new GameReferenceSet(assemblyPaths, problems);
        }

        if (!installation.IsUsable)
        {
            problems.Add(
                $"The selected installation cannot be used: {installation.Problems.FirstOrDefault() ?? "inspection reported no usable layout"}");
        }

        var managedDirectory = installation.ManagedAssemblyDirectory;

        if (string.IsNullOrEmpty(managedDirectory) || !Directory.Exists(managedDirectory))
        {
            problems.Add("The installation has no managed assembly directory to reference.");
            return new GameReferenceSet(assemblyPaths, problems);
        }

        // A mod build must not reference the same simple assembly name twice; the game's copy wins.
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                if (seenNames.Add(Path.GetFileName(path)))
                {
                    assemblyPaths.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                problems.Add($"Could not use '{path}' as a reference: {ex.Message}");
            }
        }

        try
        {
            foreach (var assembly in Directory.EnumerateFiles(managedDirectory, "*.dll").Order(StringComparer.Ordinal))
            {
                Add(assembly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Could not list the installation's assemblies: {ex.Message}");
        }

        if (installation.Layout == InstallationLayout.Waylith && includeHostRuntimeAssemblies)
        {
            // 1.7 mods target net10.0 and expect the framework to provide System.*; the game ships its own
            // assemblies but not the whole framework, so the host runtime is the only honest source available
            // to RimPulse. Unity/Mono installations ship mscorlib/System themselves, so they are not mixed in.
            foreach (var assembly in HostRuntimeAssemblies)
            {
                Add(assembly);
            }
        }

        if (assemblyPaths.Count == 0)
        {
            problems.Add($"No assemblies were found in '{managedDirectory}'.");
        }

        return new GameReferenceSet(assemblyPaths, problems);
    }

    /// <summary>
    /// The assemblies of the runtime RimPulse itself is running on (<c>TRUSTED_PLATFORM_ASSEMBLIES</c>), in
    /// the order the runtime provides them. Empty when the runtime does not expose the list.
    /// </summary>
    public static IReadOnlyList<string> HostRuntimeAssemblies
    {
        get
        {
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string list)
            {
                return [];
            }

            try
            {
                return list
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Where(File.Exists)
                    .ToArray();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}
