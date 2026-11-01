using System.Xml;

namespace RimPulse.Core.Validation;

public enum XmlCheckStatus { WellFormed, Failed, NotChecked }

/// <summary>A syntax check only, not RimWorld schema or semantic validation.</summary>
public sealed record XmlCheckResult(XmlCheckStatus Status, string Message);

/// <summary>Checks XML syntax without resolving external entities or modifying the project.</summary>
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
            using (var reader = XmlReader.Create(path, settings))
            {
                while (reader.Read()) { }
            }
            var after = new FileInfo(path);
            if (!after.Exists || after.LastWriteTimeUtc != lastWrite || after.Length != length)
                return new(XmlCheckStatus.NotChecked, "File changed during the check; check again.");
            return new(XmlCheckStatus.WellFormed, "Well-formed XML (syntax only; game semantics not checked).");
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
}
