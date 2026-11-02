using System.Text;
using System.Xml.Linq;
using RimPulse.Core.Configuration;
using RimPulse.Core.Installations;
using RimPulse.Protocol;

namespace RimPulse.Core.Runtime;

/// <summary>
/// Creates a new, separate save-data directory per launch. Profiles are kept for inspection/recovery and are
/// not silently reused or deleted; the ordinary RimWorld profile and its mod list are never written here.
/// </summary>
public static class DevelopmentProfile
{
    public static string Create(AppPaths paths, RimWorldInstallation installation, string? selectedPackageId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(installation);
        var current = InstallationProbe.Probe(installation.RootPath, installation.Source);
        if (!current.IsUsable || !current.IsSupportedVersion || current.Version != installation.Version || current.Layout != installation.Layout)
            throw new InvalidOperationException("The selected game installation changed or is not supported; re-scan before creating a development profile.");
        if (selectedPackageId is { Length: > 256 } || selectedPackageId?.Any(char.IsControl) == true)
            throw new ArgumentException("Invalid development project package ID.", nameof(selectedPackageId));

        var dataDirectory = Path.GetFullPath(Path.Combine(paths.StateDirectory, "runtime-sessions", Guid.NewGuid().ToString("N"), "data"));
        // RimWorld 1.6 parses key=value by splitting on every '='; such a path would silently fail isolation.
        if (dataDirectory.Contains('=')) throw new InvalidOperationException("The RimPulse state path contains '=' and cannot be passed safely to RimWorld's savedatafolder option.");
        var ids = new List<string> { "ludeon.rimworld", WireProtocol.CompanionPackageId };
        if (!string.IsNullOrWhiteSpace(selectedPackageId) && !ids.Contains(selectedPackageId, StringComparer.OrdinalIgnoreCase))
            ids.Add(selectedPackageId);
        var knownExpansions = new[] { "ludeon.rimworld.royalty", "ludeon.rimworld.ideology", "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey" };
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("ModsConfigData",
                new XElement("version", installation.VersionFileContents?.Trim() ?? installation.Version.ShortVersion),
                new XElement("activeMods", ids.Select(id => new XElement("li", id))),
                new XElement("knownExpansions", knownExpansions.Select(id => new XElement("li", id)))));
        var configDirectory = Path.Combine(dataDirectory, "Config");
        Directory.CreateDirectory(configDirectory);
        using var file = new FileStream(Path.Combine(configDirectory, "ModsConfig.xml"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file, new UTF8Encoding(false));
        document.Save(writer);
        return dataDirectory;
    }
}
