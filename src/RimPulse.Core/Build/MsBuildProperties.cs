using System.Collections;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RimPulse.Core.Build;

/// <summary>
/// The MSBuild properties RimPulse can evaluate for one project.
/// </summary>
/// <remarks>
/// <para>
/// RimPulse does not run MSBuild, but a project file routinely refers to properties it defines elsewhere: a
/// hint path such as <c>$(RimWorldManagedDir)/Assembly-CSharp.dll</c> is the usual way to point at the game's
/// assemblies, and those properties typically live in the project itself, in <c>Directory.Build.props</c> or in
/// a props file the project imports. Reading a hint path literally turns a resolvable reference into a
/// "the referenced assembly <c>$(RimWorldManagedDir)/Assembly-CSharp.dll</c> does not exist" error, which is
/// what this type exists to prevent.
/// </para>
/// <para>
/// It follows MSBuild's evaluation order rather than guessing: the reserved and derived properties MSBuild
/// provides, the environment, caller-supplied global properties, the nearest <c>Directory.Build.props</c>, then
/// every <c>Import</c> and <c>PropertyGroup</c> in document order, each guarded by its <c>Condition</c> when that
/// condition is one <see cref="MsBuildCondition"/> can evaluate. Global properties override environment values
/// and cannot be reassigned by the project. Other properties are expanded when defined, exactly as MSBuild
/// does, so a property may only use properties defined before it.
/// </para>
/// <para>
/// An undefined property expands to the empty string inside a condition (MSBuild's behaviour) but is kept as
/// written inside a path, where it is reported by name instead of silently becoming a nonsense relative path.
/// </para>
/// </remarks>
public sealed class MsBuildPropertySet
{
    private const int MaxImportDepth = 16;
    private const int MaxExpansionDepth = 8;
    private const int MaxDocumentCharacters = 4_000_000;
    private const string PropertyStart = "$(";

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _globalPropertyNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _evaluatedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _projectDirectory;

    private MsBuildPropertySet(string projectDirectory) => _projectDirectory = projectDirectory;

    /// <summary>
    /// Evaluates the properties in effect for <paramref name="projectFile"/>. Problems (an unreadable imported
    /// file, a condition RimPulse cannot evaluate) are added to <paramref name="problems"/> and never thrown.
    /// </summary>
    public static MsBuildPropertySet Evaluate(
        string projectFile,
        List<string>? problems = null,
        IReadOnlyDictionary<string, string>? globalProperties = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFile);

        var collected = problems ?? new List<string>();
        var path = FullPath(projectFile);
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var set = new MsBuildPropertySet(directory);

        set.SeedReservedProperties(path, directory);
        set.SeedEnvironment();
        set.SeedGlobalProperties(globalProperties);

        // MSBuild imports the nearest Directory.Build.props before the project itself, so the project can
        // override anything it sets, except an explicitly supplied global property.
        if (FindNearestDirectoryBuildProps(directory) is { } shared)
        {
            set.EvaluateFile(shared, collected, 0);
        }

        set.EvaluateFile(path, collected, 0);
        return set;
    }

    /// <summary>The value of a property as it stands after evaluation, or null when nothing defines it.</summary>
    public string? Get(string name)
        => !string.IsNullOrWhiteSpace(name) && _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>
    /// Expands every <c>$(Property)</c> reference that is defined. A reference nothing defines is left as
    /// written, so the caller can report it by name instead of resolving a path that cannot exist.
    /// </summary>
    public string Expand(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(PropertyStart, StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }

        return ExpandCore(text, 0);
    }

    /// <summary>
    /// Expands <paramref name="text"/> and reports the first property reference that is still unresolved, if
    /// any. <paramref name="expanded"/> is always the best expansion available.
    /// </summary>
    public bool TryExpand(string text, out string expanded, out string? undefinedProperty)
    {
        expanded = Expand(text);
        undefinedProperty = FirstPropertyReference(expanded);
        return undefinedProperty is null;
    }

    /// <summary>
    /// Evaluates the <c>Condition</c> attribute of an element with the properties evaluated so far. An element
    /// without a condition always applies. A condition RimPulse cannot evaluate is reported, once, and treated
    /// as false so that nothing is assumed to apply.
    /// </summary>
    /// <param name="element">The element whose <c>Condition</c> should be evaluated.</param>
    /// <param name="sourceName">The file name used when reporting, normally the project or props file name.</param>
    /// <param name="problems">Where an unsupported condition is reported; may be null.</param>
    public bool EvaluateCondition(XElement element, string sourceName, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(element);

        var condition = element.Attribute("Condition")?.Value;
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        if (MsBuildCondition.TryEvaluate(condition, this, _projectDirectory, out var result, out var reason))
        {
            return result;
        }

        problems?.Add(
            $"{sourceName}: the condition '{condition.Trim()}' on <{element.Name.LocalName}> is not evaluated by " +
            $"RimPulse ({reason ?? "unsupported"}), so the element was ignored.");

        return false;
    }

    /// <summary>Selects the first matching <c>When</c>, or the <c>Otherwise</c> branch when none match.</summary>
    internal XElement? SelectChooseBranch(XElement choose, string sourceName, List<string>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(choose);
        XElement? otherwise = null;
        foreach (var branch in choose.Elements())
        {
            if (branch.Name.LocalName == "Otherwise")
            {
                otherwise ??= branch;
                continue;
            }

            if (branch.Name.LocalName != "When")
            {
                problems?.Add($"{sourceName}: unsupported <{branch.Name.LocalName}> inside <Choose>; it was ignored.");
                continue;
            }

            if (branch.Attribute("Condition") is null)
            {
                problems?.Add($"{sourceName}: a <When> inside <Choose> has no Condition and was ignored.");
                continue;
            }

            if (EvaluateCondition(branch, sourceName, problems))
                return branch;
        }

        return otherwise;
    }

    /// <summary>Resolves a possibly relative, possibly backslash-separated path against a directory.</summary>
    internal static string? ResolvePath(string directory, string path)
    {
        try
        {
            // MSBuild files use '\' on every platform, so a verbatim path would otherwise be one long file name
            // on Linux and macOS.
            var normalized = path.Trim()
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);

            if (normalized.Length == 0)
            {
                return null;
            }

            var combined = Path.IsPathRooted(normalized) ? normalized : Path.Combine(directory, normalized);
            return Path.GetFullPath(combined);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void EvaluateFile(string filePath, List<string> problems, int depth)
    {
        var path = FullPath(filePath);

        if (depth > MaxImportDepth)
        {
            problems.Add(
                $"{Path.GetFileName(path)} is imported more than {MaxImportDepth} levels deep; RimPulse stopped following the imports.");
            return;
        }

        // An import cycle would evaluate the same file forever, and MSBuild rejects it, so it is simply skipped.
        if (!_evaluatedFiles.Add(path))
        {
            return;
        }

        var document = Load(path, problems);
        var root = document?.Root;

        if (root is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        SetThisFileProperties(path, directory);
        EvaluateElements(root, path, directory, problems, depth);
    }

    private void EvaluateElements(XElement container, string filePath, string directory, List<string> problems, int depth)
    {
        foreach (var element in container.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "PropertyGroup":
                    if (EvaluateCondition(element, Path.GetFileName(filePath), problems))
                    {
                        EvaluateProperties(element, filePath, problems);
                    }

                    break;

                case "ImportGroup":
                    if (EvaluateCondition(element, Path.GetFileName(filePath), problems))
                    {
                        foreach (var import in element.Elements().Where(child => child.Name.LocalName == "Import"))
                        {
                            EvaluateImport(import, filePath, directory, problems, depth);
                        }
                    }

                    break;

                case "Import":
                    EvaluateImport(element, filePath, directory, problems, depth);
                    break;

                case "Choose":
                    if (SelectChooseBranch(element, Path.GetFileName(filePath), problems) is { } branch)
                    {
                        EvaluateElements(branch, filePath, directory, problems, depth);
                    }
                    break;

                default:
                    // ItemGroup and everything else is read later, by BuildInputDiscovery, once every property
                    // has been evaluated.
                    break;
            }
        }
    }

    private void EvaluateProperties(XElement group, string filePath, List<string> problems)
    {
        var sourceName = Path.GetFileName(filePath);

        foreach (var property in group.Elements())
        {
            // A property with child elements is not a property; MSBuild would reject the file, so it is ignored.
            if (property.HasElements || !EvaluateCondition(property, sourceName, problems))
            {
                continue;
            }

            var name = property.Name.LocalName;
            if (_globalPropertyNames.Contains(name))
            {
                // MSBuild global properties supplied by the caller (for example, the selected game's paths)
                // cannot be overwritten by a project or one of its imports.
                continue;
            }

            // Expanded on assignment, like MSBuild: a property only sees the properties defined before it.
            _values[name] = Expand(property.Value.Trim());
        }
    }

    private void EvaluateImport(XElement import, string filePath, string directory, List<string> problems, int depth)
    {
        var declared = import.Attribute("Project")?.Value?.Trim();
        if (string.IsNullOrEmpty(declared))
        {
            return;
        }

        if (!EvaluateCondition(import, Path.GetFileName(filePath), problems))
        {
            return;
        }

        if (!TryExpand(declared, out var project, out var undefinedProperty))
        {
            problems.Add(
                $"{Path.GetFileName(filePath)}: the import '{declared}' uses the MSBuild property '{undefinedProperty}', " +
                "which nothing RimPulse reads defines, so it was ignored.");
            return;
        }

        if (project.Contains('*') || project.Contains('?'))
        {
            problems.Add(
                $"{Path.GetFileName(filePath)}: the import '{project}' uses a wildcard RimPulse does not evaluate; it was ignored.");
            return;
        }

        var resolved = ResolvePath(directory, project);
        if (resolved is null)
        {
            problems.Add($"{Path.GetFileName(filePath)}: the imported file '{project}' could not be resolved.");
            return;
        }

        if (!File.Exists(resolved))
        {
            problems.Add($"{Path.GetFileName(filePath)}: the imported file '{project}' does not exist ({resolved}).");
            return;
        }

        EvaluateFile(resolved, problems, depth + 1);
    }

    private static XDocument? Load(string path, List<string> problems)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxDocumentCharacters,
            };

            using var reader = XmlReader.Create(path, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            problems.Add($"{Path.GetFileName(path)} is not well-formed XML (line {ex.LineNumber}): {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Could not read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private void SeedReservedProperties(string projectPath, string directory)
    {
        _values["MSBuildProjectFullPath"] = projectPath;
        _values["MSBuildProjectDirectory"] = directory;
        _values["MSBuildProjectFile"] = Path.GetFileName(projectPath);
        _values["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(projectPath);
        _values["MSBuildProjectExtension"] = Path.GetExtension(projectPath);
        _values["MSBuildStartupDirectory"] = directory;
        _values["MSBuildRuntimeType"] = "Core";

        // RimPulse compiles a development build, so that is what a project's conditions are evaluated against.
        _values["Configuration"] = "Debug";
        _values["Platform"] = "AnyCPU";
        _values["OS"] = OperatingSystem.IsWindows() ? "Windows_NT" : "Unix";
    }

    /// <summary>
    /// MSBuild exposes the environment as properties, which is one of the ways a mod project gets a machine
    /// specific path in without editing the project. Reserved properties win over a variable of the same name.
    /// </summary>
    private void SeedEnvironment()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value && name.Length > 0)
            {
                _values.TryAdd(name, value);
            }
        }
    }

    /// <summary>
    /// Applies values supplied by the caller as MSBuild global properties. These override environment values
    /// and are immutable for the rest of evaluation, matching properties passed to MSBuild on the command line.
    /// </summary>
    private void SeedGlobalProperties(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null)
        {
            return;
        }

        foreach (var (name, value) in properties)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            _values[name] = value;
            _globalPropertyNames.Add(name);
        }
    }

    private void SetThisFileProperties(string path, string directory)
    {
        _values["MSBuildThisFile"] = Path.GetFileName(path);
        _values["MSBuildThisFileFullPath"] = path;
        _values["MSBuildThisFileDirectory"] = directory + Path.DirectorySeparatorChar;
        _values["MSBuildThisFileName"] = Path.GetFileNameWithoutExtension(path);
        _values["MSBuildThisFileExtension"] = Path.GetExtension(path);
    }

    private string ExpandCore(string text, int depth)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        var replaced = false;

        while (index < text.Length)
        {
            var start = text.IndexOf(PropertyStart, index, StringComparison.Ordinal);
            var end = start < 0 ? -1 : text.IndexOf(')', start + 2);

            if (start < 0 || end < 0)
            {
                builder.Append(text, index, text.Length - index);
                break;
            }

            builder.Append(text, index, start - index);

            var name = text[(start + 2)..end].Trim();

            if (name.Length > 0 && _values.TryGetValue(name, out var value))
            {
                builder.Append(value);
                replaced = true;
            }
            else
            {
                // Left as written so the caller can name the property it could not find.
                builder.Append(text, start, end - start + 1);
            }

            index = end + 1;
        }

        var result = builder.ToString();

        // A defined property may itself contain a reference (a path built from another property); MSBuild
        // expands those too. The depth limit stops a property that refers to itself.
        return replaced && depth < MaxExpansionDepth && result.Contains(PropertyStart, StringComparison.Ordinal)
            ? ExpandCore(result, depth + 1)
            : result;
    }

    private static string? FirstPropertyReference(string text)
    {
        var index = 0;

        while (true)
        {
            var start = text.IndexOf(PropertyStart, index, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            var end = text.IndexOf(')', start + 2);
            if (end < 0)
            {
                return null;
            }

            var name = text[(start + 2)..end].Trim();
            if (name.Length > 0)
            {
                return name;
            }

            index = end + 1;
        }
    }

    /// <summary>
    /// The nearest <c>Directory.Build.props</c> at or above a directory, which is the one MSBuild imports.
    /// Read-only: a missing file is not a problem, it is the normal case.
    /// </summary>
    private static string? FindNearestDirectoryBuildProps(string directory)
    {
        var current = directory;

        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var candidate = Path.Combine(current, "Directory.Build.props");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                var parent = Path.GetDirectoryName(current);
                if (parent is null || parent == current)
                {
                    return null;
                }

                current = parent;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        return null;
    }

    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
