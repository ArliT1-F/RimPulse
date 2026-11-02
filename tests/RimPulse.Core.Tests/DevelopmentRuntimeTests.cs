using System.Xml.Linq;
using RimPulse.Core.Configuration;
using RimPulse.Core.Installations;
using RimPulse.Core.Runtime;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class DevelopmentRuntimeTests
{
    [Fact]
    public void CreateProfile_UsesNewDirectoriesAndDoesNotModifyNormalConfiguration()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("game");
        FakeInstallation.CreateUnity(root);
        var normal = temp.CreateFile("normal/Config/ModsConfig.xml", "untouched");
        var paths = new AppPaths(temp.CreateDirectory("config"), temp.CreateDirectory("state"));
        var installation = InstallationProbe.Probe(root);
        var first = DevelopmentProfile.Create(paths, installation, "author.example");
        var second = DevelopmentProfile.Create(paths, installation, "author.example");
        Assert.NotEqual(first, second);
        Assert.Equal("untouched", File.ReadAllText(normal));
        var xml = XDocument.Load(Path.Combine(first, "Config", "ModsConfig.xml"));
        Assert.Equal("1.6.4851 rev1248", xml.Root!.Element("version")!.Value);
        Assert.Equal(new[] { "ludeon.rimworld", "rimpulse.companion", "author.example" },
            xml.Root.Element("activeMods")!.Elements("li").Select(x => x.Value));
        Assert.NotNull(xml.Root.Element("knownExpansions"));
    }

    [Fact]
    public void CreateProfile_RejectsAnInstallationUpdatedSinceSelection()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("game");
        FakeInstallation.CreateUnity(root);
        var selected = InstallationProbe.Probe(root);
        File.WriteAllText(Path.Combine(root, "Version.txt"), "1.6.4852 rev1249");
        var state = temp.Combine("state");
        Assert.Throws<InvalidOperationException>(() => DevelopmentProfile.Create(new AppPaths(temp.Root, state), selected, null));
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void CreateProfile_RejectsEqualsInDataPathBeforeCreatingIt()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("game");
        FakeInstallation.CreateUnity(root);
        var state = temp.Combine("state=unsafe");
        Assert.Throws<InvalidOperationException>(() => DevelopmentProfile.Create(new AppPaths(temp.Root, state), InstallationProbe.Probe(root), null));
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public void LinkProject_IsIdempotentAndNeverReplacesAnotherLocalMod()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("game");
        FakeInstallation.CreateUnity(root);
        var project = temp.CreateDirectory("project");
        Directory.CreateDirectory(Path.Combine(project, "About"));
        File.WriteAllText(Path.Combine(project, "About", "About.xml"), "<ModMetaData><name>Test</name><packageId>author.mod</packageId></ModMetaData>");
        var installation = InstallationProbe.Probe(root);
        var first = DevelopmentProjectLink.Link(installation, project);
        Assert.Equal(first, DevelopmentProjectLink.Link(installation, project));
        Assert.Equal(project, new DirectoryInfo(first).ResolveLinkTarget(true)!.FullName);
        Assert.True(File.Exists(Path.Combine(project, "About", "About.xml")));

        var another = temp.CreateDirectory("another");
        Directory.CreateDirectory(Path.Combine(another, "About"));
        File.WriteAllText(Path.Combine(another, "About", "About.xml"), "<ModMetaData><name>Other</name><packageId>author.mod</packageId></ModMetaData>");
        Assert.Throws<IOException>(() => DevelopmentProjectLink.Link(installation, another));
        Assert.Equal(project, new DirectoryInfo(first).ResolveLinkTarget(true)!.FullName);
    }

    [Fact]
    public void CompanionBuildDirectories_AreSpecificToEachInstallation()
    {
        using var temp = new TempDirectory();
        var first = temp.CreateDirectory("first-game");
        var second = temp.CreateDirectory("second-game");
        FakeInstallation.CreateUnity(first);
        FakeInstallation.CreateUnity(second);
        var paths = new AppPaths(temp.Root, temp.Combine("state"));
        var firstDirectory = CompanionInstaller.ModDirectory(paths, InstallationProbe.Probe(first));
        var secondDirectory = CompanionInstaller.ModDirectory(paths, InstallationProbe.Probe(second));
        Assert.NotEqual(firstDirectory, secondDirectory);
        Assert.Equal(firstDirectory, CompanionInstaller.ModDirectory(paths, InstallationProbe.Probe(first)));
        if (OperatingSystem.IsLinux())
        {
            var alias = Path.Combine(temp.Root, "game-alias");
            Directory.CreateSymbolicLink(alias, first);
            Assert.Equal(firstDirectory, CompanionInstaller.ModDirectory(paths, InstallationProbe.Probe(alias)));
        }
    }

    [Fact]
    public void CompanionLink_RejectsAnInstallationUpdatedSinceSelection()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("game");
        FakeInstallation.CreateUnity(root);
        var selected = InstallationProbe.Probe(root);
        File.WriteAllText(Path.Combine(root, "Version.txt"), "1.6.4852 rev1249");
        var paths = new AppPaths(temp.Root, temp.Combine("state"));
        Assert.Throws<InvalidOperationException>(() => CompanionInstaller.BuildAndLink(selected, paths));
        Assert.False(Directory.Exists(Path.Combine(root, "Mods")));
        Assert.False(Directory.Exists(paths.StateDirectory));
    }

    [Fact]
    public void CompanionLink_RefusesAnExistingDirectoryAndDifferentSymlink()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("game");
        FakeInstallation.CreateWaylith(root);
        var installation = InstallationProbe.Probe(root);
        var link = Path.Combine(root, "Mods", "RimPulse.Companion");
        Directory.CreateDirectory(link);
        File.WriteAllText(Path.Combine(link, "keep.txt"), "keep");
        var paths = new AppPaths(temp.Root, temp.Combine("state"));
        Assert.Throws<IOException>(() => CompanionInstaller.BuildAndLink(installation, paths));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(link, "keep.txt")));
        Directory.Delete(link, recursive: true);
        var other = temp.CreateDirectory("other");
        Directory.CreateSymbolicLink(link, other);
        Assert.Throws<IOException>(() => CompanionInstaller.BuildAndLink(installation, paths));
        Assert.Equal(other, new DirectoryInfo(link).ResolveLinkTarget(true)!.FullName);
    }

    [Fact]
    public async Task Start_PassesSeparateProfileAndPerChildEnvironmentOnly()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        FakeInstallation.CreateUnity(temp.Root);
        var executable = temp.CreateFile("RimWorldLinux", "#!/bin/sh\nprintf '%s\\n%s\\n' \"$1\" \"$RIMPULSE_TEST_SECRET\" > launch-args.txt\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var profile = temp.CreateDirectory("separate profile");
        const string variable = "RIMPULSE_TEST_SECRET";
        var before = Environment.GetEnvironmentVariable(variable);
        using var process = GameProcessLauncher.Start(InstallationProbe.Probe(temp.Root),
            new GameLaunchOptions(profile, new Dictionary<string, string> { [variable] = "private-value" }));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(new[] { "-savedatafolder=" + profile, "private-value" }, File.ReadAllLines(Path.Combine(temp.Root, "launch-args.txt")));
        Assert.Equal(before, Environment.GetEnvironmentVariable(variable));
    }
}
