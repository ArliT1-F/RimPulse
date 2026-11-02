using System.Security.Cryptography;
using System.Text;
using RimPulse.Core.Installations;
using RimPulse.Core.Projects;
using RimPulse.Protocol;

namespace RimPulse.Core.Runtime;

/// <summary>Opt-in local mod discovery link. Never copies or modifies the selected project.</summary>
public static class DevelopmentProjectLink
{
    public static string Link(RimWorldInstallation installation, string projectRoot)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Project linking is currently supported on Linux only.");
        var current = InstallationProbe.Probe(installation.RootPath, installation.Source);
        if (!current.IsUsable || !current.IsSupportedVersion || current.Version != installation.Version || current.Layout != installation.Layout)
            throw new InvalidOperationException("The installation changed; re-scan before linking the project.");
        var project = ModProjectInspector.Inspect(projectRoot);
        var id = project.Metadata?.PackageId;
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("The selected project needs a readable About.xml with a packageId.");
        if (string.Equals(id, WireProtocol.CompanionPackageId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "ludeon.rimworld", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected project uses a reserved package ID.");

        var modsDirectory = Path.Combine(current.RootPath, "Mods");
        Directory.CreateDirectory(modsDirectory);
        var target = Resolve(project.RootPath);
        foreach (var existing in Directory.EnumerateDirectories(modsDirectory))
        {
            var found = ModProjectInspector.Inspect(existing);
            if (!string.Equals(found.Metadata?.PackageId, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(Resolve(existing), target, StringComparison.Ordinal)) return existing;
            throw new IOException($"A different local mod already uses package ID '{id}' at '{existing}'. No link was created.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target)))[..12].ToLowerInvariant();
        var link = Path.Combine(modsDirectory, "RimPulse-Dev-" + hash);
        var alreadyLinked = CompanionInstaller.ValidateLinkDestination(link, target);
        if (!alreadyLinked) Directory.CreateSymbolicLink(link, target);
        return link;
    }

    private static string Resolve(string path) => CanonicalDirectory.Resolve(path);
}
