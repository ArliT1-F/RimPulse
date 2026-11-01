using System.Xml;
using System.Xml.Linq;
using RimPulse.Core.Installations;

namespace RimPulse.Core.Projects;

/// <summary>Metadata read from a mod's About.xml. Missing fields are reported separately as problems.</summary>
public sealed record ModMetadata(string? Name, string? PackageId, string? Author,
    IReadOnlyList<string> SupportedVersions, IReadOnlyList<ModDependency> Dependencies);

/// <summary>A declared dependency; Version is null for unversioned declarations.</summary>
public sealed record ModDependency(string? PackageId, string? DisplayName, string? Version);

/// <summary>
/// A point-in-time inspection. Detected folders are not necessarily active for any game version; the
/// interpreted <see cref="LoadFolders"/> document is what says which folders a version would load.
/// </summary>
public sealed record ModProjectInspection(string RootPath, ModMetadata? Metadata,
    IReadOnlyList<string> Folders, bool HasLoadFolders, LoadFoldersDocument LoadFolders,
    IReadOnlyList<string> Problems);

/// <summary>Inspects a mod without changing its files or assuming its content layout is fixed.</summary>
public static class ModProjectInspector
{
    private static readonly string[] ContentFolders =
        ["About", "Assemblies", "Defs", "Patches", "Textures", "Sounds", "Languages", "Source"];

    public static ModProjectInspection Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A mod directory is required.", nameof(path));
        }

        var root = InstallationProbe.NormalizePath(path);
        var problems = new List<string>();
        var folders = new List<string>();

        // Read LoadFolders.xml before anything else so an unusable version section is reported alongside
        // the rest of the project problems.
        var loadFolders = LoadFoldersReader.Read(root);
        problems.AddRange(loadFolders.Problems);

        if (!Directory.Exists(root))
        {
            problems.Add("The mod directory does not exist or is not accessible.");
            return new(root, null, folders, loadFolders.Exists, loadFolders, problems);
        }

        try
        {
            foreach (var name in ContentFolders)
            {
                if (Directory.Exists(Path.Combine(root, name)))
                {
                    folders.Add(name);
                }
            }

            // Version-named directories are shown as found, not interpreted as a load order.
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (Version.TryParse(name, out _) && !folders.Contains(name, StringComparer.Ordinal))
                {
                    folders.Add(name);
                }
            }
            folders.Sort(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Could not inspect all directories: {ex.Message}");
        }

        var about = Path.Combine(root, "About", "About.xml");
        var hasLoadFolders = loadFolders.Exists;
        if (!File.Exists(about))
        {
            problems.Add("About/About.xml was not found or is not accessible.");
            return new(root, null, folders, hasLoadFolders, loadFolders, problems);
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 };
            using var reader = XmlReader.Create(about, settings);
            var document = XDocument.Load(reader);
            var element = document.Root;
            if (element is null || element.Name != "ModMetaData")
            {
                problems.Add("About.xml must have a ModMetaData root element.");
                return new(root, null, folders, hasLoadFolders, loadFolders, problems);
            }

            static string? Value(XElement? node)
            {
                var value = node?.Value.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }

            var name = Value(element.Element("name"));
            var packageId = Value(element.Element("packageId"));
            var author = Value(element.Element("author"));
            if (author is null && element.Element("authors") is { } authors)
            {
                var names = authors.Elements("li").Select(Value).Where(v => v is not null).ToArray();
                author = names.Length == 0 ? null : string.Join(", ", names);
            }
            if (name is null) problems.Add("About.xml is missing a name.");
            if (packageId is null) problems.Add("About.xml is missing a packageId.");

            var versions = element.Element("supportedVersions")?.Elements("li")
                .Select(Value).OfType<string>().ToArray() ?? [];
            var dependencies = new List<ModDependency>();
            void AddDependencies(XElement? container, string? version)
            {
                if (container is null) return;
                foreach (var dependency in container.Elements("li"))
                {
                    var id = Value(dependency.Element("packageId"));
                    var display = Value(dependency.Element("displayName"));
                    if (id is null) problems.Add("A declared dependency is missing a packageId.");
                    dependencies.Add(new(id, display, version));
                }
            }
            AddDependencies(element.Element("modDependencies"), null);
            if (element.Element("modDependenciesByVersion") is { } byVersion)
            {
                foreach (var version in byVersion.Elements()) AddDependencies(version, version.Name.LocalName);
            }
            return new(root, new(name, packageId, author, versions, dependencies), folders, hasLoadFolders, loadFolders, problems);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            problems.Add($"Could not read About.xml: {ex.Message}");
            return new(root, null, folders, hasLoadFolders, loadFolders, problems);
        }
    }
}
