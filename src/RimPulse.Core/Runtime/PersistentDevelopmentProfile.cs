using System.Text;
using System.Xml;
using System.Xml.Linq;
using RimPulse.Core.Configuration;
using RimPulse.Core.Installations;
using RimPulse.Core.Projects;
using RimPulse.Protocol;

namespace RimPulse.Core.Runtime;

public sealed record PreparedDevelopmentProfile(string DataDirectory, ModLoadPlan Plan);

/// <summary>
/// Materializes an explicitly saved profile inside RimPulse state. Reuses saves and preferences, updating
/// only the managed mod list and the preferences exposed by the GUI. The normal game profile is never read.
/// </summary>
public static class PersistentDevelopmentProfile
{
    public static string DataDirectory(AppPaths paths, DevelopmentProfileSettings profile)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id.Length != 32 || !profile.Id.All(Uri.IsHexDigit))
            throw new ArgumentException("A valid saved profile ID is required.", nameof(profile));
        var root = Path.GetFullPath(Path.Combine(paths.StateDirectory, "development-profiles"));
        var directory = Path.GetFullPath(Path.Combine(root, profile.Id, "data"));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || directory.Contains('='))
            throw new InvalidOperationException("The development profile must be inside RimPulse state and its path cannot contain '='.");
        return directory;
    }

    public static PreparedDevelopmentProfile Prepare(AppPaths paths, RimWorldInstallation installation,
        DevelopmentProfileSettings profile, ModCatalog catalog, string? selectedProjectPath)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var current = InstallationProbe.Probe(installation.RootPath, installation.Source);
        if (!current.IsUsable || !current.IsSupportedVersion || current.Version != installation.Version || current.Layout != installation.Layout)
            throw new InvalidOperationException("The game installation changed; re-scan before preparing the profile.");
        if (!string.Equals(ModCatalogReader.Key(current.Version.ShortVersion), ModCatalogReader.Key(profile.GameVersion), StringComparison.Ordinal) ||
            !string.Equals(Path.TrimEndingDirectorySeparator(current.RootPath), Path.TrimEndingDirectorySeparator(InstallationProbe.NormalizePath(profile.InstallationPath)), StringComparison.Ordinal))
            throw new InvalidOperationException("This profile belongs to another installation or game version. Create a profile for the selected game.");
        if (profile.Game.NativeQuickStart && (current.Version.Minor != 6 || current.Layout != InstallationLayout.UnityMono))
            throw new InvalidOperationException("Native quick start is implemented for RimWorld 1.6 only. Disable it for this installation.");
        if (!profile.Game.HasValidDisplay) throw new ArgumentException("The profile has invalid display settings.");
        var project = selectedProjectPath is null ? null : ModProjectInspector.Inspect(selectedProjectPath, current.Version);
        var packageId = project?.Metadata?.PackageId;
        if (project is not null && string.IsNullOrWhiteSpace(packageId))
            throw new InvalidOperationException("The selected project needs a readable About.xml with a packageId.");
        if (project is not null && packageId is not null)
        {
            if (ModCatalogReader.Key(packageId) is "ludeon.rimworld" or WireProtocol.CompanionPackageId)
                throw new InvalidOperationException("The development project uses a reserved package ID.");
            var loaded = catalog.Mods.FirstOrDefault(mod => ModCatalogReader.Key(mod.PackageId) == ModCatalogReader.Key(packageId));
            if (loaded is null || CanonicalDirectory.Resolve(loaded.RootPath) != CanonicalDirectory.Resolve(project.RootPath))
                throw new InvalidOperationException("The selected project is not the copy the game will discover. Use Link & add selected project in Profiles and remove conflicting copies.");
        }
        var plan = ModLoadOrder.Resolve(catalog, profile.ActiveMods, current.Version, packageId);
        if (plan.Errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, plan.Errors));
        var directory = DataDirectory(paths, profile);
        // The app owns this tree; refuse a redirected config directory rather than write through it.
        for (var info = new DirectoryInfo(directory); info is not null && info.FullName != Path.GetPathRoot(info.FullName); info = info.Parent)
            if (info.Exists && info.LinkTarget is not null) throw new IOException($"Refusing to write through profile symlink '{info.FullName}'.");
        var config = Path.Combine(directory, "Config");
        Directory.CreateDirectory(config);
        var knownExpansions = catalog.Mods.Where(mod => mod.IsExpansion).Select(mod => mod.PackageId.ToLowerInvariant()).Order().ToList();
        var mods = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("ModsConfigData", new XElement("version", current.VersionFileContents?.Trim() ?? current.Version.ShortVersion),
                new XElement("activeMods", plan.ActiveMods.Select(id => new XElement("li", id))),
                // Listing every installed expansion as known prevents the game auto-enabling unselected DLC.
                new XElement("knownExpansions", knownExpansions.Select(id => new XElement("li", id)))));
        WriteAtomic(Path.Combine(config, "ModsConfig.xml"), mods);
        WriteAtomic(Path.Combine(config, "Prefs.xml"), MergePreferences(Path.Combine(config, "Prefs.xml"), profile.Game));
        return new PreparedDevelopmentProfile(directory, plan);
    }

    /// <summary>Preserves unrelated preferences and unknown future fields instead of replacing the file with a stub.</summary>
    public static XDocument MergePreferences(string path, DevelopmentGameSettings settings)
    {
        XDocument document;
        if (File.Exists(path))
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
            document = XDocument.Load(reader);
            if (document.Root?.Name != "PrefsData") throw new InvalidDataException("The profile's Prefs.xml does not have a PrefsData root.");
        }
        else document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("PrefsData"));
        var root = document.Root!;
        void Set(string name, object value)
        {
            var text = value is bool flag ? flag ? "True" : "False" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!;
            var element = root.Element(name);
            if (element is null) root.Add(new XElement(name, text)); else element.Value = text;
        }
        Set("devMode", settings.DeveloperMode);
        Set("pauseOnLoad", settings.PauseOnLoad);
        Set("runInBackground", settings.RunInBackground);
        Set("logVerbose", settings.VerboseLogging);
        Set("resetModsConfigOnCrash", settings.ResetModsOnCrash);
        Set("quickStartDevPaletteOn", settings.OpenDeveloperPalette && settings.DeveloperMode);
        Set("screenWidth", settings.ScreenWidth);
        Set("screenHeight", settings.ScreenHeight);
        Set("fullscreen", settings.Fullscreen);
        Set("uiScale", settings.UIScale);
        return document;
    }

    private static void WriteAtomic(string path, XDocument document)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
                document.Save(writer);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
