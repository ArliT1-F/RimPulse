using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using RimPulse.Core.Build;
using RimPulse.Core.Configuration;
using RimPulse.Core.Installations;
using RimPulse.Protocol;

namespace RimPulse.Core.Runtime;

/// <summary>The prepared local mod and any non-fatal compiler messages.</summary>
public sealed record CompanionInstallResult(string ModDirectory, string GameLink, bool AlreadyLinked, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds the companion against the selected, locally owned game assemblies and, only on explicit user
/// action, links it into that installation's Mods directory. Never replaces an unrelated file or folder.
/// </summary>
public static class CompanionInstaller
{
    private const string Marker = "RimPulse-generated-companion-v1";
    private const string FolderName = "RimPulse.Companion";

    /// <summary>Separate generated builds for each installation, even when they share a major/minor version.</summary>
    public static string ModDirectory(AppPaths paths, RimWorldInstallation installation)
    {
        var root = CanonicalDirectory.Resolve(installation.RootPath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..24].ToLowerInvariant();
        return Path.GetFullPath(Path.Combine(paths.StateDirectory, "companion", key, FolderName));
    }

    public static string GameLink(RimWorldInstallation installation) => Path.Combine(installation.RootPath, "Mods", FolderName);

    /// <summary>Checks that this installation links to a generated DLL built for the selected game version.</summary>
    public static bool IsInstalled(RimWorldInstallation installation, AppPaths paths)
    {
        if (!OperatingSystem.IsLinux()) return false;
        try
        {
            var modDirectory = ModDirectory(paths, installation);
            var versionDirectory = Path.Combine(modDirectory, installation.Version.ShortVersion);
            var stamp = Path.Combine(versionDirectory, ".built-against");
            return ValidateLinkDestination(GameLink(installation), modDirectory) &&
                File.Exists(Path.Combine(versionDirectory, "Assemblies", "RimPulse.Companion.dll")) &&
                File.Exists(stamp) && File.ReadAllText(stamp) == installation.Version.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static CompanionInstallResult BuildAndLink(RimWorldInstallation installation, AppPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(paths);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Companion installation is currently supported on Linux only.");

        var current = InstallationProbe.Probe(installation.RootPath, installation.Source);
        if (!current.IsUsable || !current.IsSupportedVersion || current.Version != installation.Version || current.Layout != installation.Layout ||
            (current.Version.Minor == 6 && current.Layout != InstallationLayout.UnityMono) ||
            (current.Version.Minor == 7 && current.Layout != InstallationLayout.Waylith))
            throw new InvalidOperationException("The selected installation changed or has an unsupported version/layout. Re-scan before installing.");

        var modDirectory = ModDirectory(paths, current);
        var link = GameLink(current);
        ValidateLinkDestination(link, modDirectory); // Fail before compiling or touching the installation.
        var markerPath = Path.Combine(modDirectory, ".rimpulse-owned");
        if (Directory.Exists(modDirectory) && Directory.EnumerateFileSystemEntries(modDirectory).Any() &&
            (!File.Exists(markerPath) || File.ReadAllText(markerPath) != Marker))
            throw new IOException($"'{modDirectory}' is not marked as a RimPulse-generated companion; it was not changed.");

        var references = GameReferenceResolver.Resolve(current);
        if (references.Problems.Count > 0 || references.IsEmpty)
            throw new InvalidOperationException("Cannot resolve companion references: " + string.Join("; ", references.Problems));
        // Do not expose RimPulse's own protocol types to the game compile: the companion includes its
        // own copy, and must not acquire a runtime dependency on the desktop application.
        var managedReferences = references.AssemblyPaths
            .Where(path => !Path.GetFileName(path).StartsWith("RimPulse", StringComparison.OrdinalIgnoreCase))
            .Where(IsManagedAssembly).ToArray();
        var buildDirectory = Path.Combine(paths.StateDirectory, "companion-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(buildDirectory);
        try
        {
            var sourceFiles = new[]
            {
                Extract("RimPulse.Companion.Protocol.cs", Path.Combine(buildDirectory, "Protocol.cs")),
                Extract("RimPulse.Companion.Companion.cs", Path.Combine(buildDirectory, "Companion.cs")),
                Extract($"RimPulse.Companion.RimWorld{current.Version.Minor + 10}.cs", Path.Combine(buildDirectory, "GameAdapter.cs")),
            };
            var temporaryAssembly = Path.Combine(buildDirectory, "RimPulse.Companion.dll");
            var result = ModCompiler.Compile(new CompileRequest("RimPulse.Companion", sourceFiles, managedReferences,
                OutputPath: temporaryAssembly, EmitDebugSymbols: false), cancellationToken);
            if (!result.Succeeded || result.Problems.Count > 0)
            {
                var errors = result.Problems.Concat(result.Diagnostics.Where(d => d.Severity == BuildDiagnosticSeverity.Error)
                    .Select(d => d.Display)).Take(12);
                throw new InvalidOperationException("Could not compile the companion against this installation: " + string.Join("; ", errors));
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(modDirectory);
            WriteAtomic(markerPath, Marker);
            WriteAtomic(Path.Combine(modDirectory, "About", "About.xml"), AboutXml);
            WriteAtomic(Path.Combine(modDirectory, "LoadFolders.xml"), LoadFoldersXml);
            var versionDirectory = Path.Combine(modDirectory, current.Version.ShortVersion);
            var assemblyDirectory = Path.Combine(versionDirectory, "Assemblies");
            Directory.CreateDirectory(assemblyDirectory);
            File.Move(temporaryAssembly, Path.Combine(assemblyDirectory, "RimPulse.Companion.dll"), overwrite: true);
            WriteAtomic(Path.Combine(versionDirectory, ".built-against"), current.Version.ToString());

            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var alreadyLinked = ValidateLinkDestination(link, modDirectory);
            if (!alreadyLinked) Directory.CreateSymbolicLink(link, modDirectory);
            return new CompanionInstallResult(modDirectory, link, alreadyLinked,
                result.Diagnostics.Where(d => d.Severity == BuildDiagnosticSeverity.Warning).Select(d => d.Display).ToArray());
        }
        finally
        {
            try { Directory.Delete(buildDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>True only when the path is already our link; an unrelated existing path is refused.</summary>
    internal static bool ValidateLinkDestination(string link, string target)
    {
        var info = new DirectoryInfo(link);
        var linkTarget = info.LinkTarget;
        if (linkTarget is null)
        {
            if (Directory.Exists(link) || File.Exists(link))
                throw new IOException($"'{link}' already exists and is not the RimPulse link; it was not changed.");
            return false;
        }

        var absoluteTarget = Path.GetFullPath(linkTarget, Path.GetDirectoryName(link)!);
        if (!string.Equals(Path.TrimEndingDirectorySeparator(absoluteTarget),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)), StringComparison.Ordinal))
            throw new IOException($"'{link}' already links somewhere else; it was not changed.");
        return true;
    }

    private static bool IsManagedAssembly(string path)
    {
        try { _ = AssemblyName.GetAssemblyName(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or FileLoadException) { return false; }
    }

    private static string Extract(string resource, string destination)
    {
        using var input = typeof(CompanionInstaller).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Bundled companion source '{resource}' is missing.");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        return destination;
    }

    private static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private const string AboutXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <ModMetaData>
          <name>RimPulse Companion</name>
          <author>RimPulse contributors</author>
          <packageId>rimpulse.companion</packageId>
          <supportedVersions><li>1.6</li><li>1.7</li></supportedVersions>
          <description>Reports the game startup mod snapshot to a RimPulse-launched local session. Dormant otherwise.</description>
        </ModMetaData>
        """;

    private const string LoadFoldersXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <loadFolders>
          <v1.6><li>1.6</li></v1.6>
          <v1.7><li>1.7</li></v1.7>
        </loadFolders>
        """;
}
