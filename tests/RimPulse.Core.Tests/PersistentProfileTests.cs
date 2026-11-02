using System.Xml.Linq;
using RimPulse.Core.Configuration;
using RimPulse.Core.Game;
using RimPulse.Core.Installations;
using RimPulse.Core.Platform;
using RimPulse.Core.Projects;
using RimPulse.Core.Runtime;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class PersistentProfileTests
{
    private static void AddMod(string root, string id, string name = "Test", string extra = "")
    {
        Directory.CreateDirectory(Path.Combine(root, "About"));
        File.WriteAllText(Path.Combine(root, "About", "About.xml"),
            $"<ModMetaData><name>{name}</name><packageId>{id}</packageId><supportedVersions><li>1.6</li><li>1.7</li></supportedVersions>{extra}</ModMetaData>");
    }

    private static DevelopmentProfileSettings Profile(RimWorldInstallation installation) => new()
    {
        InstallationPath = installation.RootPath, GameVersion = installation.Version.ShortVersion,
    };

    [Fact]
    public void Catalog_ReadsOfficialDlcLocalModsAndEverySteamLibraryWithoutEnablingThem()
    {
        using var temp = new TempDirectory();
        var game = FakeInstallation.CreateUnity(temp.CreateDirectory("library", "steamapps", "common", "RimWorld"));
        AddMod(Path.Combine(game, "Data", "Core"), "Ludeon.RimWorld", "Core");
        AddMod(Path.Combine(game, "Data", "Royalty"), "Ludeon.RimWorld.Royalty", "Royalty");
        AddMod(Path.Combine(game, "Mods", "Local"), "author.local");
        AddMod(temp.CreateDirectory("library", "steamapps", "workshop", "content", "294100", "1"), "author.workshop");
        AddMod(temp.CreateDirectory("other-library", "steamapps", "workshop", "content", "294100", "2"), "author.other");
        temp.CreateFile(".steam/steam/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders" { "0" { "path" "{{temp.Combine("other-library")}}" } }
            """);
        var model = InstallationProbe.Probe(game);
        Assert.True(model.IsUsable, string.Join(";", model.Problems));
        var catalog = ModCatalogReader.Read(model, temp.Root, HostPlatform.Linux);
        Assert.Equal(5, catalog.Mods.Count);
        Assert.Equal(2, catalog.Mods.Count(mod => mod.Source == ModSource.Workshop));
        Assert.True(Assert.Single(catalog.Mods.Where(mod => mod.IsExpansion)).IsExpansion);
        Assert.Empty(catalog.Problems);
    }

    [Fact]
    public void Catalog_DeduplicatesProjectSymlinksByCanonicalDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        var game = FakeInstallation.CreateUnity(temp.CreateDirectory("game"));
        temp.CreateDirectory("game", "Mods");
        AddMod(Path.Combine(game, "Data", "Core"), "ludeon.rimworld");
        var project = temp.CreateDirectory("project"); AddMod(project, "author.project");
        Directory.CreateSymbolicLink(Path.Combine(game, "Mods", "Linked"), project);
        var catalog = ModCatalogReader.Read(InstallationProbe.Probe(game), temp.Root, HostPlatform.Linux);
        Assert.Single(catalog.Mods.Where(mod => mod.PackageId == "author.project"));
        Assert.Equal(ModSource.Local, catalog.Mods.Single(mod => mod.PackageId == "author.project").Source);
    }

    [Fact]
    public void LoadOrder_IncludesTransitiveDependenciesAndHonorsForcedOrder()
    {
        using var temp = new TempDirectory();
        var game = FakeInstallation.CreateUnity(temp.Root);
        AddMod(Path.Combine(game, "Data", "Core"), "ludeon.rimworld");
        AddMod(Path.Combine(game, "Mods", "Companion"), "rimpulse.companion");
        AddMod(Path.Combine(game, "Mods", "Mod"), "author.mod", extra: "<modDependencies><li><packageId>author.base</packageId></li></modDependencies>");
        AddMod(Path.Combine(game, "Mods", "Base"), "author.base", extra: "<forceLoadAfter><li>author.library</li></forceLoadAfter>");
        AddMod(Path.Combine(game, "Mods", "Library"), "author.library_steam");
        var catalog = ModCatalogReader.Read(InstallationProbe.Probe(game), temp.Root, HostPlatform.Linux);
        var plan = ModLoadOrder.Resolve(catalog, ["author.mod", "author.library_steam"], new GameVersion(1, 6), null);
        Assert.True(plan.Errors.Count == 0, string.Join(";",plan.Errors));
        Assert.Equal("ludeon.rimworld", plan.ActiveMods[0]);
        Assert.True(plan.ActiveMods.ToList().IndexOf("author.library_steam") < plan.ActiveMods.ToList().IndexOf("author.base"));
        Assert.True(plan.ActiveMods.ToList().IndexOf("author.base") < plan.ActiveMods.ToList().IndexOf("author.mod"));
        Assert.Contains("rimpulse.companion", plan.ActiveMods);
    }

    [Fact]
    public void LoadOrder_ReportsMissingIncompatibleAndCyclicMods()
    {
        var empty = new ModCatalog([], []);
        Assert.Contains(ModLoadOrder.Resolve(empty, ["missing"], new GameVersion(1, 6), null).Errors, error => error.Contains("Missing mod"));
        var rules = new ModLoadRules([], [], ["b"], [], []);
        var mods = new[]
        {
            new InstalledMod("a", "A", "/a", ModSource.Local, ["1.6"], [], rules, []),
            new InstalledMod("b", "B", "/b", ModSource.Local, ["1.6"], [], rules with { ForceLoadAfter = ["a"], IncompatibleWith = ["a"] }, []),
        };
        var plan = ModLoadOrder.Resolve(new ModCatalog(mods, []), ["a", "b"], new GameVersion(1, 6), null);
        Assert.Contains(plan.Errors, error => error.Contains("cycle"));
        Assert.Contains(plan.Errors, error => error.Contains("incompatible"));
    }

    [Fact]
    public void Metadata_ExactVersionRulesReplaceCommonRules()
    {
        using var temp = new TempDirectory();
        AddMod(temp.Root, "author.mod", extra: """
            <modDependencies><li><packageId>old.base</packageId></li></modDependencies>
            <modDependenciesByVersion><v1.6><li><packageId>new.base</packageId></li></v1.6></modDependenciesByVersion>
            <loadAfter><li>old.base</li></loadAfter>
            <loadAfterByVersion><v1.6><li>new.base</li></v1.6></loadAfterByVersion>
            """);
        var result = ModProjectInspector.Inspect(temp.Root, new GameVersion(1, 6));
        Assert.Equal("new.base", Assert.Single(result.Metadata!.Dependencies).PackageId);
        Assert.Equal(["new.base"], result.Metadata.LoadRules!.LoadAfter);
        Assert.Equal(2, ModProjectInspector.Inspect(temp.Root).Metadata!.Dependencies.Count);
    }

    [Fact]
    public void Prepare_ReusesDataWritesOnlySelectedDlcAndPreservesUnknownPreferences()
    {
        using var temp = new TempDirectory();
        var game = FakeInstallation.CreateUnity(temp.CreateDirectory("game"));
        AddMod(Path.Combine(game, "Data", "Core"), "ludeon.rimworld");
        AddMod(Path.Combine(game, "Data", "Royalty"), "ludeon.rimworld.royalty");
        AddMod(Path.Combine(game, "Data", "Ideology"), "ludeon.rimworld.ideology");
        AddMod(Path.Combine(game, "Mods", "Companion"), "rimpulse.companion");
        var installation = InstallationProbe.Probe(game);
        var paths = new AppPaths(temp.CreateDirectory("config"), temp.CreateDirectory("state"));
        var profile = Profile(installation); profile.ActiveMods.Add("ludeon.rimworld.royalty");
        var catalog = ModCatalogReader.Read(installation, temp.Root, HostPlatform.Linux);
        var first = PersistentDevelopmentProfile.Prepare(paths, installation, profile, catalog, null);
        var save = temp.CreateFile(Path.GetRelativePath(temp.Root, Path.Combine(first.DataDirectory, "Saves", "keep.rws")), "keep my colony");
        temp.CreateFile(Path.GetRelativePath(temp.Root, Path.Combine(first.DataDirectory, "Config", "Prefs.xml")),
            "<PrefsData><volumeMusic>0.25</volumeMusic><futureSetting>keep</futureSetting></PrefsData>");
        var second = PersistentDevelopmentProfile.Prepare(paths, installation, profile, catalog, null);
        Assert.Equal(first.DataDirectory, second.DataDirectory);
        Assert.Equal("keep my colony", File.ReadAllText(save));
        var mods = XDocument.Load(Path.Combine(first.DataDirectory, "Config", "ModsConfig.xml"));
        Assert.Equal(["ludeon.rimworld", "rimpulse.companion", "ludeon.rimworld.royalty"], mods.Root!.Element("activeMods")!.Elements().Select(node => node.Value));
        Assert.Equal(["ludeon.rimworld.ideology", "ludeon.rimworld.royalty"], mods.Root.Element("knownExpansions")!.Elements().Select(node => node.Value));
        var prefs = XDocument.Load(Path.Combine(first.DataDirectory, "Config", "Prefs.xml"));
        Assert.Equal("0.25", prefs.Root!.Element("volumeMusic")!.Value);
        Assert.Equal("keep", prefs.Root.Element("futureSetting")!.Value);
        Assert.Equal("True", prefs.Root.Element("devMode")!.Value);
        Assert.Equal("False", prefs.Root.Element("resetModsConfigOnCrash")!.Value);
    }

    [Fact]
    public void Prepare_InvalidProfileDoesNotCreateOrRewriteData()
    {
        using var temp = new TempDirectory();
        var game = FakeInstallation.CreateUnity(temp.CreateDirectory("game"));
        var installation = InstallationProbe.Probe(game);
        var paths = new AppPaths(temp.Root, temp.Combine("state"));
        var profile = Profile(installation);
        Assert.Throws<InvalidOperationException>(() => PersistentDevelopmentProfile.Prepare(paths, installation, profile, new ModCatalog([], []), null));
        Assert.False(Directory.Exists(paths.StateDirectory));
        profile.Id = "../../escape";
        Assert.Throws<ArgumentException>(() => PersistentDevelopmentProfile.DataDirectory(paths, profile));
        profile.Id = new string('a', 32); profile.Game = profile.Game with { NativeQuickStart = true };
        File.WriteAllText(Path.Combine(game, "Version.txt"), "1.7.0");
        // A fake Waylith layout would be required to reach the quick-start guard; version mismatch is also fatal.
        Assert.Throws<InvalidOperationException>(() => PersistentDevelopmentProfile.Prepare(paths, installation, profile, new ModCatalog([], []), null));
    }

    [Fact]
    public void Prepare_RejectsNativeQuickStartOnWaylithBeforeWriting()
    {
        using var temp = new TempDirectory();
        var game = FakeInstallation.CreateWaylith(temp.CreateDirectory("game"));
        var installation = InstallationProbe.Probe(game);
        var paths = new AppPaths(temp.Root, temp.Combine("state"));
        var profile = Profile(installation); profile.Game = profile.Game with { NativeQuickStart = true };
        Assert.Throws<InvalidOperationException>(() => PersistentDevelopmentProfile.Prepare(paths, installation, profile, new ModCatalog([], []), null));
        Assert.False(Directory.Exists(paths.StateDirectory));
    }

    [Fact]
    public async Task Configuration_RoundTripsProfilesAndMigratesOldDefaults()
    {
        using var temp = new TempDirectory();
        var store = new ConfigurationStore(temp.Combine("config.json"));
        var profile = new DevelopmentProfileSettings { Name = "  Animals  ", InstallationPath = "/game", GameVersion = "1.6" };
        await store.SaveAsync(new RimPulseConfiguration { DevelopmentProfiles = [profile], SelectedDevelopmentProfileId = profile.Id,
            BuildConfiguration = "Release", EmitDebugSymbols = false, MinimumLogLevel = "Warning" });
        var loaded = (await store.LoadAsync()).Configuration;
        Assert.Equal(3, loaded.SchemaVersion);
        Assert.Equal("Animals", Assert.Single(loaded.DevelopmentProfiles).Name);
        Assert.Equal("Release", loaded.BuildConfiguration);
        Assert.False(loaded.EmitDebugSymbols);
        Assert.Equal("Warning", loaded.MinimumLogLevel);
        temp.CreateFile("old.json", """{"schemaVersion":2,"selectedProjectPath":"/mods/Example"}""");
        var old = await new ConfigurationStore(temp.Combine("old.json")).LoadAsync();
        Assert.True(old.Configuration.AutoDetectOnStartup);
        Assert.Empty(old.Configuration.DevelopmentProfiles);
        Assert.Equal("/mods/Example", old.Configuration.SelectedProjectPath);
    }

    [Fact]
    public async Task Launcher_QuickTestIsExplicitAndNeverAppliedToNormalLaunch()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory(); FakeInstallation.CreateUnity(temp.Root);
        var executable = temp.CreateFile("RimWorldLinux", "#!/bin/sh\nprintf '%s\\n' \"$#\" \"$@\" > args.txt\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var installation = InstallationProbe.Probe(temp.Root);
        using (var process = GameProcessLauncher.Start(installation, new GameLaunchOptions())) await process.WaitForExitAsync();
        Assert.Equal("0", Assert.Single(File.ReadAllLines(temp.Combine("args.txt"))));
        using (var process = GameProcessLauncher.Start(installation, new GameLaunchOptions(NativeQuickStart: true))) await process.WaitForExitAsync();
        Assert.Equal(["1", "-quicktest"], File.ReadAllLines(temp.Combine("args.txt")));
    }

    [Fact]
    public void Compiler_ReleaseOmitsImplicitDebugAndCanOmitSymbols()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Conditional.cs", "#if DEBUG\n#error DEBUG must not be implicit in Release\n#endif\npublic static class Mod { }");
        var references = RimPulse.Core.Build.GameReferenceResolver.HostRuntimeAssemblies
            .Where(path => Path.GetFileName(path) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "netstandard.dll").ToArray();
        var output = temp.Combine("Mod.dll");
        var request = new RimPulse.Core.Build.CompileRequest("Mod", [source], references, OutputPath: output,
            Release: true, EmitDebugSymbols: false);
        var result = RimPulse.Core.Build.ModCompiler.Compile(request);
        Assert.True(result.Succeeded, string.Join(";", result.Diagnostics.Select(d => d.Display)));
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(Path.ChangeExtension(output, ".pdb")));
        Assert.Null(result.PdbPath);
        Assert.False(RimPulse.Core.Build.ModCompiler.Compile(request with { Release = false }).Succeeded);
    }

    [Fact]
    public void Detector_DisablingWellKnownScansStillRestoresManualPaths()
    {
        using var temp = new TempDirectory();
        var manual = FakeInstallation.CreateUnity(temp.CreateDirectory("manual"));
        FakeInstallation.CreateUnity(temp.CreateDirectory(".steam", "steam", "steamapps", "common", "RimWorld"));
        temp.CreateFile(".steam/steam/steamapps/appmanifest_294100.acf", """
            "AppState" { "installdir" "RimWorld" }
            """);
        var detector = new RimWorldInstallationDetector(new InstallationDetectorOptions
        {
            Platform = HostPlatform.Linux, HomeDirectory = temp.Root,
        });
        Assert.Equal(2, detector.Detect([manual]).Count);
        var restored = Assert.Single(detector.Detect([manual], scanWellKnownLocations: false));
        Assert.Equal(manual, restored.RootPath);
        Assert.Equal(InstallationSource.Manual, restored.Source);
    }

}
