using System.Xml;
using System.Xml.Linq;

namespace RimPulse.Core.Build;

/// <summary>
/// One C# project RimPulse would compile: the source files, the file references declared through
/// <c>HintPath</c> and the other projects it references, all resolved to absolute paths.
/// </summary>
/// <remarks>
/// RimPulse reads a documented subset of MSBuild instead of evaluating it. Properties and items guarded by
/// <c>Condition</c> are ignored, and wildcard patterns other than <c>*.cs</c> and <c>**/*.cs</c> are reported
/// rather than guessed at. A project that needs more than that is a known limitation, not a silent failure.
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

    public static BuildInputInspection Discover(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

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
            var unit = ReadUnit(projectFile, problems);
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

    private static ModCompileUnit? ReadUnit(string projectFile, List<string> problems)
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
        var assemblyName = Property(root, "AssemblyName")
                           ?? Path.GetFileNameWithoutExtension(projectFile);

        var sources = new SortedSet<string>(StringComparer.Ordinal);
        var explicitCompileItems = Items(root, "Compile")
            .Where(item => item.Attribute("Include") is not null)
            .ToArray();

        foreach (var item in explicitCompileItems)
        {
            foreach (var file in Expand(item.Attribute("Include")!.Value, directory, displayName, problems))
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
            foreach (var file in Expand(item.Attribute("Remove")!.Value, directory, displayName, problems))
            {
                sources.Remove(file);
            }
        }

        var targetFramework = Property(root, "TargetFramework")
                              ?? Property(root, "TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        targetFramework = string.IsNullOrWhiteSpace(targetFramework) ? null : targetFramework.Trim();

        var references = new List<string>();
        foreach (var item in Items(root, "Reference"))
        {
            var hintPath = item.Elements().FirstOrDefault(child => child.Name.LocalName == "HintPath")?.Value.Trim();
            if (string.IsNullOrEmpty(hintPath))
            {
                // A plain <Reference Include="System" /> is covered by the game's own assemblies.
                continue;
            }

            var resolved = ResolveRelativePath(directory, hintPath);
            if (resolved is null)
            {
                problems.Add($"{displayName}: the hint path '{hintPath}' could not be resolved.");
                continue;
            }

            if (!File.Exists(resolved))
            {
                problems.Add($"{displayName}: the referenced assembly '{hintPath}' does not exist ({resolved}).");
                continue;
            }

            references.Add(resolved);
        }

        // A PackageReference is not resolved by MSBuild here: RimPulse cannot run NuGet restore. Packages that are
        // already restored on this machine are read from the global packages folder; the rest are reported, so a
        // missing Harmony (or any other package) shows up as one clear line instead of hundreds of CS0246 errors.
        foreach (var item in Items(root, "PackageReference"))
        {
            var packageId = item.Attribute("Include")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(packageId))
            {
                continue;
            }

            if (item.Attribute("Condition") is not null)
            {
                problems.Add($"{displayName}: the package reference '{packageId}' is conditional, and RimPulse does not evaluate conditions; it was not referenced.");
                continue;
            }

            var version = item.Attribute("Version")?.Value.Trim()
                          ?? item.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value.Trim();

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
            var include = item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var resolved = ResolveRelativePath(directory, include);
            if (resolved is null || !File.Exists(resolved))
            {
                problems.Add($"{displayName}: the referenced project '{include}' does not exist.");
                continue;
            }

            projectReferences.Add(resolved);
        }

        var defines = (Property(root, "DefineConstants") ?? string.Empty)
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
            Property(root, "LangVersion"),
            string.Equals(Property(root, "AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase),
            IsNullableEnabled(Property(root, "Nullable")));
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

    private static string? Property(XElement root, string name)
    {
        foreach (var group in root.Elements().Where(element => element.Name.LocalName == "PropertyGroup"))
        {
            // Conditional property groups depend on how the project is built; RimPulse does not evaluate them.
            if (group.Attribute("Condition") is not null)
            {
                continue;
            }

            foreach (var property in group.Elements().Where(element => element.Name.LocalName == name))
            {
                var value = property.Value.Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
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
