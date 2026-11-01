using System.Xml;
using System.Xml.Linq;
using RimPulse.Core.Game;

namespace RimPulse.Core.Projects;

/// <summary>One conditional attribute on a <c>LoadFolders.xml</c> entry.</summary>
/// <remarks>
/// The semantics follow the RimWorld wiki (Modding Tutorials / Mod Folder Structure): the values of
/// <c>IfModActive</c>, <c>IfModActiveAny</c> and <c>IfModNotActive</c> are comma separated lists that are
/// treated as "or", <c>IfModActiveAll</c> (added in RimWorld 1.6) is treated as "and", and several attributes
/// on the same entry must all pass.
/// </remarks>
public enum LoadFolderConditionKind
{
    /// <summary><c>IfModActive</c> — at least one of the listed package IDs must be active.</summary>
    Active = 0,

    /// <summary><c>IfModActiveAny</c> — at least one of the listed package IDs must be active.</summary>
    ActiveAny = 1,

    /// <summary><c>IfModActiveAll</c> — every listed package ID must be active.</summary>
    ActiveAll = 2,

    /// <summary><c>IfModNotActive</c> — none of the listed package IDs may be active.</summary>
    NotActive = 3,
}

/// <summary>A single condition on a load-folder entry, as written in the file.</summary>
public sealed record LoadFolderCondition(
    LoadFolderConditionKind Kind,
    string AttributeName,
    IReadOnlyList<string> PackageIds)
{
    /// <summary>True when this condition holds for the supplied set of active package IDs.</summary>
    public bool IsSatisfiedBy(IReadOnlySet<string> activePackageIds)
    {
        ArgumentNullException.ThrowIfNull(activePackageIds);

        return Kind switch
        {
            LoadFolderConditionKind.ActiveAll => PackageIds.All(activePackageIds.Contains),
            LoadFolderConditionKind.NotActive => !PackageIds.Any(activePackageIds.Contains),
            _ => PackageIds.Any(activePackageIds.Contains),
        };
    }

    public string Display => $"{AttributeName}=\"{string.Join(", ", PackageIds)}\"";
}

/// <summary>
/// One <c>&lt;li&gt;</c> entry of a version section: the folder, plus the conditions that gate it.
/// <c>/</c> means the mod root.
/// </summary>
public sealed record LoadFolderEntry(string Path, IReadOnlyList<LoadFolderCondition> Conditions)
{
    public bool IsRoot => Path == "/";

    public bool HasConditions => Conditions.Count > 0;

    public string ConditionDisplay => HasConditions
        ? string.Join(" and ", Conditions.Select(condition => condition.Display))
        : string.Empty;

    /// <summary>True when every condition holds. An unconditional entry always holds.</summary>
    public bool IsSatisfiedBy(IReadOnlySet<string> activePackageIds)
        => Conditions.All(condition => condition.IsSatisfiedBy(activePackageIds));
}

/// <summary>One <c>&lt;v1.6&gt;</c>-style section of <c>LoadFolders.xml</c>, in file order.</summary>
public sealed record LoadFolderSection(string ElementName, string VersionText, IReadOnlyList<LoadFolderEntry> Entries);

/// <summary>
/// The parsed <c>LoadFolders.xml</c>. <see cref="FilePath"/> is null when the mod has no such file, in which
/// case RimWorld's default versioned-folder rules apply — rules RimPulse does not model.
/// </summary>
public sealed record LoadFoldersDocument(
    string? FilePath,
    IReadOnlyList<LoadFolderSection> Sections,
    IReadOnlyList<string> Problems)
{
    /// <summary>True when the mod ships a readable <c>LoadFolders.xml</c>.</summary>
    public bool Exists => FilePath is not null;

    /// <summary>The version sections found, e.g. <c>v1.6</c>, <c>v1.7</c>.</summary>
    public IReadOnlyList<string> SectionNames => Sections.Select(section => section.ElementName).ToArray();

    /// <summary>The section for a game version (<c>1.6</c> matches <c>&lt;v1.6&gt;</c>), or null.</summary>
    public LoadFolderSection? FindSection(GameVersion version)
    {
        if (!version.IsKnown)
        {
            return null;
        }

        return Sections.FirstOrDefault(section =>
            string.Equals(section.VersionText, version.ShortVersion, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The folders RimPulse would expect to be loaded for one game version, plus the entries whose conditions did
/// not hold for the package IDs the caller supplied. This is a projection of the file, not a report of what
/// the game actually loaded.
/// </summary>
public sealed record LoadFolderPlan(
    string? VersionText,
    IReadOnlyList<string> Folders,
    IReadOnlyList<LoadFolderEntry> Skipped,
    IReadOnlyList<string> Problems);

/// <summary>
/// Reads <c>LoadFolders.xml</c> and projects it onto a game version. Nothing here launches the game, so the
/// result is a documented interpretation of the file, not a confirmation of the game's load order.
/// </summary>
public static class LoadFoldersReader
{
    public const string FileName = "LoadFolders.xml";

    private static readonly (string AttributeName, LoadFolderConditionKind Kind)[] ConditionAttributes =
    [
        ("IfModActive", LoadFolderConditionKind.Active),
        ("IfModActiveAny", LoadFolderConditionKind.ActiveAny),
        ("IfModActiveAll", LoadFolderConditionKind.ActiveAll),
        ("IfModNotActive", LoadFolderConditionKind.NotActive),
    ];

    /// <summary>Reads the file if it exists. Never throws for missing, unreadable or malformed files.</summary>
    public static LoadFoldersDocument Read(string modRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modRootPath);

        string path;
        try
        {
            path = Path.Combine(Path.GetFullPath(modRootPath), FileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new LoadFoldersDocument(null, [], [$"Could not resolve the project directory: {ex.Message}"]);
        }

        bool exists;
        try
        {
            exists = File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LoadFoldersDocument(null, [], [$"Could not look for {FileName}: {ex.Message}"]);
        }

        if (!exists)
        {
            return new LoadFoldersDocument(null, [], []);
        }

        var problems = new List<string>();

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_000_000,
            };

            XDocument document;
            using (var reader = XmlReader.Create(path, settings))
            {
                document = XDocument.Load(reader);
            }

            var root = document.Root;
            if (root is null || !root.Name.LocalName.Equals("loadFolders", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{FileName} must have a <loadFolders> root element.");
                return new LoadFoldersDocument(path, [], problems);
            }

            var sections = new List<LoadFolderSection>();

            foreach (var sectionElement in root.Elements())
            {
                var elementName = sectionElement.Name.LocalName;
                var versionText = ParseVersionElementName(elementName);

                if (versionText is null)
                {
                    problems.Add(
                        $"{FileName} contains a section <{elementName}> that is not a game version section " +
                        "(expected <v1.6>, <v1.7>, …); it was ignored.");
                    continue;
                }

                if (sections.Exists(section =>
                        string.Equals(section.VersionText, versionText, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"{FileName} declares the version section <{elementName}> more than once; later entries still apply.");
                }

                var entries = new List<LoadFolderEntry>();

                foreach (var item in sectionElement.Elements())
                {
                    if (!item.Name.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"Section <{elementName}> contains a <{item.Name.LocalName}> element; only <li> entries are read.");
                        continue;
                    }

                    var entryPath = NormalizeEntryPath(item.Value);
                    if (entryPath is null)
                    {
                        problems.Add($"Section <{elementName}> contains an empty <li> entry or a folder path outside the mod; it was ignored.");
                        continue;
                    }

                    var conditions = new List<LoadFolderCondition>();

                    foreach (var attribute in item.Attributes())
                    {
                        if (attribute.IsNamespaceDeclaration)
                        {
                            continue;
                        }

                        var kind = FindConditionKind(attribute.Name.LocalName);
                        if (kind is null)
                        {
                            problems.Add(
                                $"Section <{elementName}>, entry '{entryPath}': the attribute " +
                                $"'{attribute.Name.LocalName}' is not a load-folder condition and was ignored.");
                            continue;
                        }

                        var packageIds = attribute.Value
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(NormalizePackageId)
                            .Where(id => id.Length > 0)
                            .ToArray();

                        if (packageIds.Length == 0)
                        {
                            problems.Add(
                                $"Section <{elementName}>, entry '{entryPath}': '{attribute.Name.LocalName}' " +
                                "lists no package IDs and was ignored.");
                            continue;
                        }

                        conditions.Add(new LoadFolderCondition(kind.Value, CanonicalAttributeName(kind.Value), packageIds));
                    }

                    entries.Add(new LoadFolderEntry(entryPath, conditions));
                }

                sections.Add(new LoadFolderSection(elementName, versionText, entries));
            }

            return new LoadFoldersDocument(path, sections, problems);
        }
        catch (XmlException ex)
        {
            problems.Add($"{FileName} is not well-formed XML (line {ex.LineNumber}, column {ex.LinePosition}): {ex.Message}");
            return new LoadFoldersDocument(path, [], problems);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Could not read {FileName}: {ex.Message}");
            return new LoadFoldersDocument(path, [], problems);
        }
    }

    /// <summary>
    /// Projects the file onto a game version using the supplied active package IDs. The caller decides which
    /// package IDs count as active: RimPulse knows the mod itself and its declared dependencies, not the game's
    /// real mod list.
    /// </summary>
    public static LoadFolderPlan Evaluate(
        LoadFoldersDocument document,
        GameVersion version,
        IEnumerable<string>? activePackageIds)
    {
        ArgumentNullException.ThrowIfNull(document);

        var active = new HashSet<string>(
            (activePackageIds ?? []).Select(NormalizePackageId).Where(id => id.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var problems = new List<string>();

        if (!document.Exists)
        {
            problems.Add(
                $"{FileName} is not present. RimWorld then uses its default versioned-folder rules, " +
                "which RimPulse does not model, so no folders are listed.");
            return new LoadFolderPlan(null, [], [], problems);
        }

        if (!version.IsKnown)
        {
            problems.Add("The selected installation has no confirmed RimWorld version, so no version section can be selected.");
            return new LoadFolderPlan(null, [], [], problems);
        }

        var section = document.FindSection(version);
        if (section is null)
        {
            var available = document.Sections.Count == 0
                ? "none"
                : string.Join(", ", document.SectionNames);
            problems.Add(
                $"{FileName} has no <v{version.ShortVersion}> section (sections present: {available}). " +
                $"RimWorld's documentation says {FileName} supersedes the default versioned-folder rules for every " +
                "version except 1.0, so RimPulse cannot list folders for this version — verify in game.");
            return new LoadFolderPlan(version.ShortVersion, [], [], problems);
        }

        var folders = new List<string>();
        var skipped = new List<LoadFolderEntry>();

        foreach (var entry in section.Entries)
        {
            if (entry.IsSatisfiedBy(active))
            {
                if (!folders.Contains(entry.Path, StringComparer.Ordinal))
                {
                    folders.Add(entry.Path);
                }
            }
            else
            {
                skipped.Add(entry);
            }
        }

        return new LoadFolderPlan(version.ShortVersion, folders, skipped, problems);
    }

    /// <summary>
    /// Normalises a <c>IfModActive</c> value. RimWorld 1.5 and earlier needed a <c>_steam</c> suffix to target
    /// the Steam copy of a mod, so the suffix is ignored when comparing against active package IDs.
    /// </summary>
    public static string NormalizePackageId(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.EndsWith("_steam", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^"_steam".Length]
            : trimmed;
    }

    private static string? ParseVersionElementName(string elementName)
    {
        if (elementName.Length < 4 || elementName[0] is not ('v' or 'V'))
        {
            return null;
        }

        var candidate = elementName[1..];
        var parts = candidate.Split('.');

        return parts.Length == 2
               && int.TryParse(parts[0], out _)
               && int.TryParse(parts[1], out _)
            ? candidate
            : null;
    }

    /// <summary>Returns a '/'-separated relative path, "/" for the mod root, or null when the entry is unusable.</summary>
    private static string? NormalizeEntryPath(string? value)
    {
        var trimmed = value?.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed == "/" || trimmed == ".")
        {
            return "/";
        }

        // '/' is the separator RimWorld uses regardless of host platform, so do not let the host's notion of
        // a rooted path decide: reject anything that starts with a separator or a drive letter.
        if (trimmed.StartsWith('/') || (trimmed.Length > 1 && trimmed[1] == ':'))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                return null;
            }

            segments.Add(segment);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static LoadFolderConditionKind? FindConditionKind(string attributeName)
    {
        foreach (var (name, kind) in ConditionAttributes)
        {
            if (string.Equals(name, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }

    private static string CanonicalAttributeName(LoadFolderConditionKind kind) => kind switch
    {
        LoadFolderConditionKind.ActiveAll => "IfModActiveAll",
        LoadFolderConditionKind.ActiveAny => "IfModActiveAny",
        LoadFolderConditionKind.NotActive => "IfModNotActive",
        _ => "IfModActive",
    };
}
