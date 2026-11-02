using System.Xml;
using System.Xml.Linq;

namespace RimPulse.Core.Validation;

public enum XmlCheckStatus { WellFormed, Failed, NotChecked }

/// <summary>A conservative syntax and basic Defs-structure check, not RimWorld schema validation.</summary>
public sealed record XmlCheckResult(XmlCheckStatus Status, string Message);

/// <summary>Checks XML syntax and a small set of safe structural rules without resolving external entities or modifying the project.</summary>
public static class XmlFileValidator
{
    public static XmlCheckResult Check(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return new(XmlCheckStatus.NotChecked, "Invalid relative path.");
        try
        {
            var root = Path.GetFullPath(rootPath);
            var path = Path.GetFullPath(relativePath, root);
            var relative = Path.GetRelativePath(root, path);
            if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
                return new(XmlCheckStatus.NotChecked, "Path is outside the project.");
            if (!Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                return new(XmlCheckStatus.NotChecked, "Not an XML file.");
            if (!File.Exists(path)) return new(XmlCheckStatus.NotChecked, "File is missing or inaccessible.");

            var before = new FileInfo(path);
            var lastWrite = before.LastWriteTimeUtc;
            var length = before.Length;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4_000_000,
            };
            XDocument document;
            using (var reader = XmlReader.Create(path, settings))
                document = XDocument.Load(reader, LoadOptions.SetLineInfo);

            var after = new FileInfo(path);
            if (!after.Exists || after.LastWriteTimeUtc != lastWrite || after.Length != length)
                return new(XmlCheckStatus.NotChecked, "File changed during the check; check again.");

            var structuralIssue = CheckDefsStructure(relative, document);
            return structuralIssue is null
                ? new(XmlCheckStatus.WellFormed, IsUnderDefs(relative)
                    ? "Well-formed XML; basic Defs structure checked (game semantics not checked)."
                    : "Well-formed XML (syntax only; game semantics not checked).")
                : new(XmlCheckStatus.Failed, structuralIssue);
        }
        catch (XmlException ex)
        {
            return new(XmlCheckStatus.Failed, $"XML check failed at line {ex.LineNumber}, column {ex.LinePosition}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new(XmlCheckStatus.NotChecked, $"Could not read XML: {ex.Message}");
        }
    }

    private static string? CheckDefsStructure(string relativePath, XDocument document)
    {
        if (!IsUnderDefs(relativePath)) return null;
        var root = document.Root;
        if (root is null) return "Defs XML must have a root element named <Defs> or <Patch>.";

        // RimWorld definition files use <Defs>; patch files are also conventionally stored in Defs
        // folders and use <Patch>. Other roots are almost always a misplaced document.
        if (root.Name.LocalName != "Defs" && root.Name.LocalName != "Patch")
            return $"Expected <Defs> or <Patch> as the root element under a Defs folder, found <{root.Name.LocalName}>.";
        if (!root.Name.NamespaceName.Equals(string.Empty, StringComparison.Ordinal))
            return "RimWorld Defs XML does not support an XML namespace on its root element.";
        if (root.Name.LocalName == "Patch") return null;

        foreach (var definitionGroup in root.Elements().GroupBy(element => element.Name.LocalName, StringComparer.Ordinal))
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var definition in definitionGroup)
            {
                var names = definition.Elements()
                    .Where(element => element.Name.LocalName == "defName")
                    .Select(element => element.Value.Trim())
                    .Where(name => name.Length > 0)
                    .ToArray();
                if (names.Length == 0) continue; // Abstract/named defs and partial patterns are game-specific.
                if (names.Length > 1)
                    return AtLine(definition, $"<{definition.Name.LocalName}> contains more than one <defName> element.");
                if (!seen.Add(names[0]))
                    return AtLine(definition, $"Duplicate defName '{names[0]}' in <{definitionGroup.Key}> definitions in this file.");
            }
        }
        return null;
    }

    private static bool IsUnderDefs(string relativePath) =>
        relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals("Defs", StringComparison.OrdinalIgnoreCase));

    private static string AtLine(XElement element, string message) =>
        element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
            ? $"{message} (line {lineInfo.LineNumber}, column {lineInfo.LinePosition})."
            : message;
}
