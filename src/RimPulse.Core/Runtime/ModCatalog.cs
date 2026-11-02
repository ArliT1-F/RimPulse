using RimPulse.Core.Game;
using RimPulse.Core.Installations;
using RimPulse.Core.Installations.Steam;
using RimPulse.Core.Platform;
using RimPulse.Core.Projects;

namespace RimPulse.Core.Runtime;

public enum ModSource { Official, Local, Workshop }

/// <summary>A locally installed mod, read from its About.xml. No download or game assembly is executed.</summary>
public sealed record InstalledMod(string PackageId, string Name, string RootPath, ModSource Source,
    IReadOnlyList<string> SupportedVersions, IReadOnlyList<ModDependency> Dependencies, ModLoadRules Rules,
    IReadOnlyList<string> Problems)
{
    public bool IsCore => ModCatalogReader.Key(PackageId) == "ludeon.rimworld";
    public bool IsExpansion => Source == ModSource.Official && !IsCore &&
        PackageId.StartsWith("ludeon.rimworld.", StringComparison.OrdinalIgnoreCase);
    public string SourceLabel => Source + (IsExpansion ? " · DLC" : IsCore ? " · required" : string.Empty);
}

public sealed record ModCatalog(IReadOnlyList<InstalledMod> Mods, IReadOnlyList<string> Problems);

/// <summary>Reads official content, local Mods and downloaded Workshop mods from every known Steam library.</summary>
public static class ModCatalogReader
{
    public static string Key(string id)
    {
        var key = id.Trim().ToLowerInvariant();
        return key.EndsWith("_steam", StringComparison.Ordinal) ? key[..^6] : key;
    }

    public static ModCatalog Read(RimWorldInstallation installation, string homeDirectory, HostPlatform platform,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var problems = new List<string>();
        var candidates = new List<InstalledMod>();
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A manually selected installation need not have Steam's client installed in a standard location.
        for (var directory = new DirectoryInfo(installation.RootPath); directory is not null; directory = directory.Parent)
        {
            if (directory.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                libraries.Add(directory.Parent!.FullName);
                break;
            }
        }
        foreach (var root in SteamLibraryReader.GetSteamRootCandidates(homeDirectory, platform))
        {
            try
            {
                foreach (var library in SteamLibraryReader.ReadLibraryFolders(root)) libraries.Add(library);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ValveKeyValuesFormatException)
            {
                problems.Add($"Could not read Steam libraries at '{root}': {ex.Message}");
            }
        }
        var folders = new List<(string Path, ModSource Source)>
        {
            (Path.Combine(installation.RootPath, "Data"), ModSource.Official),
            (Path.Combine(installation.RootPath, "Mods"), ModSource.Local),
        };
        foreach (var library in libraries.Order())
            folders.Add((Path.Combine(library, "steamapps", "workshop", "content", "294100"), ModSource.Workshop));
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (folder, source) in folders)
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Could not list '{folder}': {ex.Message}");
                continue;
            }
            foreach (var path in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!seenPaths.Add(CanonicalDirectory.Resolve(path))) continue;
                    var inspection = ModProjectInspector.Inspect(path, installation.Version);
                    var metadata = inspection.Metadata;
                    if (metadata?.PackageId is not { Length: > 0 and <= 256 } id || id.Any(char.IsControl))
                    {
                        // A Workshop download may contain a wrapper folder, not a mod. Do not recursively
                        // guess its layout or present an incomplete mod as available to the game.
                        if (File.Exists(Path.Combine(path, "About", "About.xml")))
                            problems.Add($"No usable package ID in '{path}'.");
                        continue;
                    }
                    candidates.Add(new InstalledMod(id, metadata.Name ?? id, inspection.RootPath, source,
                        metadata.SupportedVersions, metadata.Dependencies,
                        metadata.LoadRules ?? new([], [], [], [], []), inspection.Problems));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    problems.Add($"Could not inspect '{path}': {ex.Message}");
                }
            }
        }
        // Match the game's precedence: official content, local mods, then Workshop. Canonical-path dedup
        // above makes a project's discovery symlink equivalent to its real directory.
        var mods = new List<InstalledMod>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in candidates.OrderBy(mod => mod.Source).ThenBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (ids.Add(Key(mod.PackageId))) mods.Add(mod);
            else problems.Add($"Duplicate package ID '{mod.PackageId}' at '{mod.RootPath}' is shadowed by the higher-precedence copy.");
            if (mods.Count == 2048)
            {
                problems.Add("The catalog is limited to 2,048 mods, matching the companion snapshot limit.");
                break;
            }
        }
        return new ModCatalog(mods, problems);
    }
}

public sealed record ModLoadPlan(IReadOnlyList<string> ActiveMods, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>Resolves declared dependencies and load order; never downloads mods or silently enables unselected DLC.</summary>
public static class ModLoadOrder
{
    public static ModLoadPlan Resolve(ModCatalog catalog, IEnumerable<string> requested, GameVersion version, string? selectedPackageId = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var byId = catalog.Mods.ToDictionary(mod => ModCatalogReader.Key(mod.PackageId), StringComparer.Ordinal);
        var order = requested.Select(ModCatalogReader.Key).Distinct(StringComparer.Ordinal).ToList();
        var active = order.ToHashSet(StringComparer.Ordinal);
        void Activate(string id) { if (active.Add(id)) order.Add(id); }
        Activate("ludeon.rimworld");
        Activate("rimpulse.companion");
        if (!string.IsNullOrWhiteSpace(selectedPackageId)) Activate(ModCatalogReader.Key(selectedPackageId));
        var errors = new List<string>();
        var warnings = new List<string>(catalog.Problems);
        // Closure is explicit: dependencies are added, not just reported after an incomplete launch.
        var queue = new Queue<string>(active);
        while (queue.TryDequeue(out var id))
        {
            if (!byId.TryGetValue(id, out var mod))
            {
                errors.Add($"Missing mod '{id}'. Link the project or install the mod, then refresh the catalog.");
                continue;
            }
            foreach (var dependency in mod.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(dependency.PackageId))
                {
                    errors.Add($"'{mod.Name}' declares a dependency without a package ID.");
                    continue;
                }
                var key = ModCatalogReader.Key(dependency.PackageId);
                if (!active.Contains(key)) { Activate(key); queue.Enqueue(key); }
            }
            if (mod.SupportedVersions.Count > 0 && !mod.IsCore && !mod.SupportedVersions.Any(declared =>
                string.Equals(declared.TrimStart('v', 'V'), version.ShortVersion, StringComparison.OrdinalIgnoreCase)))
                warnings.Add($"'{mod.Name}' does not declare support for {version.ShortVersion}; the game decides whether it loads.");
        }
        var nodes = active.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var mod in nodes) edges[ModCatalogReader.Key(mod.PackageId)] = new(StringComparer.Ordinal);
        void Edge(string before, string after) { if (edges.ContainsKey(before) && edges.ContainsKey(after)) edges[before].Add(after); }
        foreach (var mod in nodes)
        {
            var id = ModCatalogReader.Key(mod.PackageId);
            foreach (var dependency in mod.Dependencies.Where(d => d.PackageId is not null)) Edge(ModCatalogReader.Key(dependency.PackageId!), id);
            foreach (var other in mod.Rules.ForceLoadAfter) Edge(ModCatalogReader.Key(other), id);
            foreach (var other in mod.Rules.ForceLoadBefore) Edge(id, ModCatalogReader.Key(other));
            foreach (var other in mod.Rules.IncompatibleWith)
                if (active.Contains(ModCatalogReader.Key(other))) errors.Add($"'{mod.Name}' is incompatible with '{other}'.");
            warnings.AddRange(mod.Problems.Select(problem => $"{mod.Name}: {problem}"));
        }
        bool Reaches(string from, string to)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>();
            pending.Push(from);
            while (pending.TryPop(out var current))
                if (visited.Add(current))
                {
                    if (current == to) return true;
                    foreach (var next in edges[current]) pending.Push(next);
                }
            return false;
        }
        // Advisory rules are applied only if they do not introduce a cycle. Hard rules and dependencies
        // are never discarded to make an invalid profile appear launchable.
        foreach (var mod in nodes)
        {
            var id = ModCatalogReader.Key(mod.PackageId);
            foreach (var other in mod.Rules.LoadAfter)
            {
                var key = ModCatalogReader.Key(other);
                if (edges.ContainsKey(key) && !Reaches(id, key)) Edge(key, id);
            }
            foreach (var other in mod.Rules.LoadBefore)
            {
                var key = ModCatalogReader.Key(other);
                if (edges.ContainsKey(key) && !Reaches(key, id)) Edge(id, key);
            }
        }
        var priority = order.Select((id, index) => (Id: id, Index: id == "ludeon.rimworld" ? -1 : index))
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        // edges maps a prerequisite to its dependents: count incoming edges, not outgoing edges.
        var indegree = edges.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var dependents in edges.Values)
            foreach (var dependent in dependents) indegree[dependent]++;
        var ready = new List<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var ordered = new List<string>();
        while (ready.Count > 0)
        {
            ready.Sort((a, b) => priority[a].CompareTo(priority[b]));
            var id = ready[0];
            ready.RemoveAt(0);
            ordered.Add(byId[id].PackageId.ToLowerInvariant());
            foreach (var dependent in edges[id])
                if (--indegree[dependent] == 0) ready.Add(dependent);
        }
        if (ordered.Count != nodes.Count)
            errors.Add("The enabled mods have a dependency or forced-load-order cycle. Remove one of the conflicting mods.");
        return new ModLoadPlan(ordered, errors.Distinct().ToArray(), warnings.Distinct().ToArray());
    }

}
