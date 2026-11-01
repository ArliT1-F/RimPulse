using System.Xml;
using System.Xml.Linq;

namespace RimPulse.Core.Build;

/// <summary>
/// One C# project RimPulse would compile: the source files, the file references declared through
/// <c>HintPath</c> and the other projects it references, all resolved to absolute paths.
/// </summary>
/// <remarks>
/// RimPulse evaluates the MSBuild subset a mod project actually uses — properties (including the nearest
/// <c>Directory.Build.props</c> and the files a project imports), <c>Condition</c>s it understands and the
/// <c>Configuration</c> it builds — and reports everything outside it: a condition it cannot evaluate, a
/// wildcard pattern other than <c>*.cs</c> and <c>**/*.cs</c>, a package it cannot restore. A project that
/// needs more than that is a known limitation, not a silent failure.
/// </remarks>
public sealed record ModCompileUnit(
    string ProjectFilePath,
    string ProjectDirectory,
    string AssemblyName,
    string? TargetFramework,
    IReadOnlyList<string> SourceFiles,
    IReadOnlyList<string> ReferencePaths,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> DefineConstants,
    string? LangVersion,
    bool AllowUnsafe,
    bool NullableEnabled)
{
    /// <summary>True when the project has at least one source file to compile.</summary>
    public bool HasSources => SourceFiles.Count > 0;
}

/// <summary>Every compile unit found under a mod root, ordered so that referenced projects come first.</summary>
public sealed record BuildInputInspection(IReadOnlyList<ModCompileUnit> Units, IReadOnlyList<string> Problems)
{
    public bool HasUnits => Units.Count > 0;

    public bool HasSources => Units.Any(unit => unit.HasSources);

    public int SourceFileCount => Units.Sum(unit => unit.SourceFiles.Count);
}

/// <summary>Finds the C# projects of a mod directory without building or modifying anything.</summary>
public static class BuildInputDiscovery
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".vscode", "node_modules", "packages", "artifacts",
    };

    private const int MaxProjects = 16;
    private const int MaxDirectoryDepth = 6;

    /// <param name="projectRoot">The mod directory to search for .csproj files.</param>
    /// <param name="assemblySearchDirectories">
    /// Directories searched for a referenced assembly whose declared <c>HintPath</c> does not exist on this
    /// machine, normally the selected game's managed folder. A project that points at another machine's
    /// installation would otherwise fail over a file RimPulse already has.
    /// </param>
    public static BuildInputInspection Discover(
        string projectRoot,
        IReadOnlyList<string>? assemblySearchDirectories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var searchDirectories = assemblySearchDirectories ?? Array.Empty<string>();
        var problems = new List<string>();
        string root;

        try
        {
            root = Path.GetFullPath(projectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problems.Add($"The mod directory could not be resolved: {ex.Message}");
            return new BuildInputInspection([], problems);
        }

        if (!Directory.Exists(root))
        {
            problems.Add("The mod directory does not exist or is not accessible.");
            return new BuildInputInspection([], problems);
        }

        var projectFiles = new List<string>();

        try
        {
            EnumerateProjectFiles(root, 0, projectFiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Could not search for .csproj files: {ex.Message}");
        }

        if (projectFiles.Count == 0)
        {
            problems.Add("No .csproj file was found, so there is no C# project to compile (XML-only mods have none).");
            return new BuildInputInspection([], problems);
        }

        projectFiles.Sort(StringComparer.Ordinal);

        if (projectFiles.Count > MaxProjects)
        {
            problems.Add($"{projectFiles.Count} projects were found; only the first {MaxProjects} are used.");
            projectFiles.RemoveRange(MaxProjects, projectFiles.Count - MaxProjects);
        }

        var units = new List<ModCompileUnit>();

        foreach (var projectFile in projectFiles)
        {
            var unit = ReadUnit(projectFile, searchDirectories, problems);
            if (unit is not null)
            {
                units.Add(unit);
            }
        }

        return new BuildInputInspection(OrderByProjectReference(units, problems), problems);
    }

    private static void EnumerateProjectFiles(string directory, int depth, List<string> projectFiles)
    {
        if (depth > MaxDirectoryDepth || projectFiles.Count > MaxProjects)
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.csproj").Order(StringComparer.Ordinal))
        {
            projectFiles.Add(file);
        }

        foreach (var child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(child);
            if (IgnoredDirectoryNames.Contains(name))
            {
                continue;
            }

            EnumerateProjectFiles(child, depth + 1, projectFiles);
        }
    }

    private static ModCompileUnit? ReadUnit(
        string projectFile,
        IReadOnlyList<string> assemblySearchDirectories,
        List<string> problems)
    {
        XDocument document;

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4_000_000,
            };

            using var reader = XmlReader.Create(projectFile, settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            problems.Add($"{Path.GetFileName(projectFile)} is not well-formed XML (line {ex.LineNumber}): {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Could not read {Path.GetFileName(projectFile)}: {ex.Message}");
            return null;
        }

        var root = document.Root;
        if (root is null)
        {
            problems.Add($"{Path.GetFileName(projectFile)} has no root element.");
            return null;
        }

        var directory = Path.GetDirectoryName(projectFile) ?? string.Empty;
        var displayName = Path.GetFileName(projectFile);

        // Properties are evaluated before the items that use them, so a hint path written as
        // '$(RimWorldManagedDir)/Assembly-CSharp.dll' is a real path by the time it is read.
        var properties = MsBuildPropertySet.Evaluate(projectFile, problems);

        string? GetProperty(string name)
        {
            var value = properties.Get(name)?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        var assemblyName = GetProperty("AssemblyName")
                           ?? Path.GetFileNameWithoutExtension(projectFile);

        var sources = new SortedSet<string>(StringComparer.Ordinal);
        var explicitCompileItems = Items(root, "Compile")
            .Where(item => item.Attribute("Include") is not null
                           && properties.EvaluateCondition(item, displayName, problems))
            .ToArray();

        foreach (var item in explicitCompileItems)
        {
            var include = properties.Expand(item.Attribute("Include")!.Value);

            foreach (var file in Expand(include, directory, displayName, problems))
            {
                sources.Add(file);
            }
        }

        // SDK-style projects list no <Compile> items at all: the sources are every .cs under the project.
        if (explicitCompileItems.Length == 0)
        {
            foreach (var file in EnumerateSourceFiles(directory))
            {
                sources.Add(file);
            }
        }

        foreach (var item in Items(root, "Compile").Where(item => item.Attribute("Remove") is not null))
        {
            if (!properties.EvaluateCondition(item, displayName, problems))
            {
                continue;
            }

            var remove = properties.Expand(item.Attribute("Remove")!.Value);

            foreach (var file in Expand(remove, directory, displayName, problems))
            {
                sources.Remove(file);
            }
        }

        var targetFramework = GetProperty("TargetFramework")
                              ?? GetProperty("TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        targetFramework = string.IsNullOrWhiteSpace(targetFramework) ? null : targetFramework.Trim();

        var references = new List<string>();
        foreach (var item in Items(root, "Reference"))
        {
            if (!properties.EvaluateCondition(item, displayName, problems))
            {
                continue;
            }

            var hintPath = item.Elements().FirstOrDefault(child => child.Name.LocalName == "HintPath")?.Value;
            if (string.IsNullOrWhiteSpace(hintPath))
            {
                // A plain <Reference Include="System" /> is covered by the game's own assemblies.
                continue;
            }

            AddHintPathReference(
                displayName,
                hintPath,
                directory,
                properties,
                assemblySearchDirectories,
                references,
                problems);
        }

        // A PackageReference is not resolved by MSBuild here: RimPulse cannot run NuGet restore. Packages that are
        // already restored on this machine are read from the global packages folder; the rest are reported, so a
        // missing Harmony (or any other package) shows up as one clear line instead of hundreds of CS0246 errors.
        foreach (var item in Items(root, "PackageReference"))
        {
            var declaredId = item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(declaredId))
            {
                continue;
            }

            if (!properties.EvaluateCondition(item, displayName, problems))
            {
                continue;
            }

            var declaredVersion = item.Attribute("Version")?.Value
                                  ?? item.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value;

            if (!properties.TryExpand(declaredId.Trim(), out var packageId, out var undefinedProperty))
            {
                problems.Add(
                    $"{displayName}: the package reference '{declaredId.Trim()}' uses the MSBuild property " +
                    $"'{undefinedProperty}', which nothing RimPulse reads defines; it was not referenced.");
                continue;
            }

            string? version = null;

            if (!string.IsNullOrWhiteSpace(declaredVersion))
            {
                if (!properties.TryExpand(declaredVersion.Trim(), out var expandedVersion, out undefinedProperty))
                {
                    problems.Add(
                        $"{displayName}: the version of the package reference '{packageId}' uses the MSBuild " +
                        $"property '{undefinedProperty}', which nothing RimPulse reads defines; it was not referenced.");
                    continue;
                }

                version = expandedVersion;
            }

            var resolution = NuGetPackageResolver.Resolve(packageId, version, targetFramework);

            if (resolution.Resolved)
            {
                references.AddRange(resolution.AssemblyPaths);
            }
            else if (resolution.Problem is not null)
            {
                problems.Add($"{displayName}: {resolution.Problem}");
            }
        }

        var projectReferences = new List<string>();
        foreach (var item in Items(root, "ProjectReference"))
        {
            if (!properties.EvaluateCondition(item, displayName, problems))
            {
                continue;
            }

            var include = item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var declared = properties.Expand(include);

            var resolved = ResolveRelativePath(directory, declared);
            if (resolved is null || !File.Exists(resolved))
            {
                problems.Add($"{displayName}: the referenced project '{declared}' does not exist.");
                continue;
            }

            projectReferences.Add(resolved);
        }

        var defines = (GetProperty("DefineConstants") ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        return new ModCompileUnit(
            projectFile,
            directory,
            assemblyName,
            targetFramework,
            sources.ToArray(),
            references,
            projectReferences,
            defines,
            GetProperty("LangVersion"),
            string.Equals(GetProperty("AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase),
            IsNullableEnabled(GetProperty("Nullable")));
    }

    private static bool IsNullableEnabled(string? value)
        => value is not null
           && (value.Equals("enable", StringComparison.OrdinalIgnoreCase)
               || value.Equals("warnings", StringComparison.OrdinalIgnoreCase)
               || value.Equals("annotations", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var file in Directory.EnumerateFiles(current, "*.cs").Order(StringComparer.Ordinal))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (!IgnoredDirectoryNames.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    /// <summary>
    /// Expands the two wildcard shapes RimPulse understands, plus literal paths. Anything else is reported:
    /// evaluating arbitrary MSBuild globs would be guessing.
    /// </summary>
    private static IEnumerable<string> Expand(string pattern, string directory, string projectName, List<string> problems)
    {
        var trimmed = pattern.Trim().Replace('\\', '/');

        if (trimmed.Length == 0)
        {
            yield break;
        }

        if (trimmed is "*.cs" or "**/*.cs")
        {
            var search = trimmed is "*.cs" ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories;
            string[] files;

            try
            {
                files = Directory.GetFiles(directory, "*.cs", search);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{projectName}: could not expand '{pattern}': {ex.Message}");
                yield break;
            }

            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                if (!file.Split(Path.DirectorySeparatorChar).Any(IgnoredDirectoryNames.Contains))
                {
                    yield return Path.GetFullPath(file);
                }
            }

            yield break;
        }

        if (trimmed.Contains('*') || trimmed.Contains('?'))
        {
            problems.Add($"{projectName}: the item pattern '{pattern}' uses a wildcard RimPulse does not evaluate; it was ignored.");
            yield break;
        }

        var resolved = ResolveRelativePath(directory, trimmed);
        if (resolved is null)
        {
            problems.Add($"{projectName}: the item path '{pattern}' could not be resolved.");
            yield break;
        }

        yield return resolved;
    }

    private static string? ResolveRelativePath(string directory, string path)
    {
        try
        {
            // MSBuild files use '\' as the separator on every platform, so a verbatim HintPath or
            // ProjectReference would otherwise be a single, nonexistent file name on Linux and macOS.
            var normalized = path.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            var combined = Path.IsPathRooted(normalized) ? normalized : Path.Combine(directory, normalized);
            return Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves one <c>HintPath</c> reference. A path written in terms of a property is expanded first; when the
    /// declared file is not on this machine (a project pointing at another machine's install, or a property
    /// nothing RimPulse reads defines) the same file name is looked for in the search directories before the
    /// reference is dropped, because dropping it would turn one clear line into a wall of CS0246 errors.
    /// </summary>
    private static void AddHintPathReference(
        string displayName,
        string hintPath,
        string directory,
        MsBuildPropertySet properties,
        IReadOnlyList<string> assemblySearchDirectories,
        List<string> references,
        List<string> problems)
    {
        var declared = hintPath.Trim();

        if (!properties.TryExpand(declared, out var expanded, out var undefinedProperty))
        {
            // The file name itself is still readable, so the installation can still supply the assembly.
            if (TryFindInSearchDirectories(assemblySearchDirectories, FileNameOf(expanded), out var replacement))
            {
                references.Add(replacement);
                problems.Add(
                    $"{displayName}: '{declared}' uses the MSBuild property '{undefinedProperty}', which nothing " +
                    $"RimPulse reads defines; the installation's '{Path.GetFileName(replacement)}' is referenced instead.");
            }
            else
            {
                problems.Add(
                    $"{displayName}: '{declared}' uses the MSBuild property '{undefinedProperty}', which nothing " +
                    "RimPulse reads defines, so the reference was not added.");
            }

            return;
        }

        var resolved = ResolveRelativePath(directory, expanded);
        if (resolved is null)
        {
            problems.Add($"{displayName}: the hint path '{expanded}' could not be resolved.");
            return;
        }

        if (File.Exists(resolved))
        {
            references.Add(resolved);
            return;
        }

        if (TryFindInSearchDirectories(assemblySearchDirectories, Path.GetFileName(resolved), out var fallback))
        {
            references.Add(fallback);
            problems.Add(
                $"{displayName}: the hint path '{expanded}' does not exist on this machine; " +
                $"'{fallback}' is referenced instead.");
            return;
        }

        problems.Add($"{displayName}: the referenced assembly '{expanded}' does not exist ({resolved}).");
    }

    /// <summary>The file name of a path written with either separator, on any platform.</summary>
    private static string FileNameOf(string path) => Path.GetFileName(path.Replace('\\', '/'));

    private static bool TryFindInSearchDirectories(
        IReadOnlyList<string> directories,
        string fileName,
        out string path)
    {
        foreach (var directory in directories)
        {
            if (string.IsNullOrEmpty(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory, fileName);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // A malformed search directory is skipped; the caller reports the missing reference.
            }
        }

        path = string.Empty;
        return false;
    }

    private static IEnumerable<XElement> Items(XElement root, string name)
        => root.Elements()
            .Where(element => element.Name.LocalName == "ItemGroup")
            .SelectMany(group => group.Elements())
            .Where(element => element.Name.LocalName == name);

    /// <summary>Puts referenced projects before their dependents; a cycle is reported and kept stable.</summary>
    private static IReadOnlyList<ModCompileUnit> OrderByProjectReference(
        IReadOnlyList<ModCompileUnit> units, List<string> problems)
    {
        if (units.Count <= 1)
        {
            return units;
        }

        var byPath = new Dictionary<string, ModCompileUnit>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in units)
        {
            byPath[unit.ProjectFilePath] = unit;
        }

        foreach (var unit in units)
        {
            foreach (var reference in unit.ProjectReferences)
            {
                if (!byPath.ContainsKey(reference))
                {
                    problems.Add(
                        $"{Path.GetFileName(unit.ProjectFilePath)} references {Path.GetFileName(reference)}, " +
                        "which is not a project RimPulse discovered under this mod directory.");
                }
            }
        }

        var ordered = new List<ModCompileUnit>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ModCompileUnit unit)
        {
            if (visited.Contains(unit.ProjectFilePath))
            {
                return;
            }

            if (!visiting.Add(unit.ProjectFilePath))
            {
                problems.Add($"The project reference graph contains a cycle through {Path.GetFileName(unit.ProjectFilePath)}.");
                return;
            }

            foreach (var reference in unit.ProjectReferences)
            {
                if (byPath.TryGetValue(reference, out var dependency))
                {
                    Visit(dependency);
                }
            }

            visiting.Remove(unit.ProjectFilePath);
            visited.Add(unit.ProjectFilePath);
            ordered.Add(unit);
        }

        foreach (var unit in units)
        {
            Visit(unit);
        }

        return ordered;
    }
}
