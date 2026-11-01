using RimPulse.Core.Diagnostics;
using RimPulse.Core.Installations;
using RimPulse.Core.Platform;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class InstallationDetectorTests
{
    private static InstallationDetectorOptions CreateOptions(TempDirectory temp, string? environmentRimWorldDirectory = null)
        => new()
        {
            Platform = HostPlatform.Linux,
            HomeDirectory = temp.Root,
            EnvironmentRimWorldDirectory = environmentRimWorldDirectory,
            ScanSteamLibraries = true,
            ScanGogDirectories = true,
        };

    [Fact]
    public void Detect_FindsInstallationThroughSteamLibrary()
    {
        using var temp = new TempDirectory();
        var steamRoot = temp.CreateDirectory(".steam", "steam");
        var library = temp.CreateDirectory("second-library");
        var installRoot = FakeInstallation.CreateUnity(temp.CreateDirectory("second-library", "steamapps", "common", "RimWorld"));

        temp.CreateFile(".steam/steam/steamapps/libraryfolders.vdf", $$"""
            "libraryfolders"
            {
                "0" { "path" "{{steamRoot}}" }
                "1" { "path" "{{library}}" }
            }
            """);

        temp.CreateFile("second-library/steamapps/appmanifest_294100.acf", """
            "AppState"
            {
                "installdir"    "RimWorld"
            }
            """);

        var detector = new RimWorldInstallationDetector(CreateOptions(temp), new DiagnosticLog());
        var installations = detector.Detect();

        Assert.Single(installations);
        Assert.Equal(Path.GetFullPath(installRoot), installations[0].RootPath);
        Assert.Equal(InstallationSource.Steam, installations[0].Source);
        Assert.Equal(InstallationLayout.UnityMono, installations[0].Layout);
        Assert.Equal(1, installations[0].Version.Major);
        Assert.Equal(6, installations[0].Version.Minor);
    }

    [Fact]
    public void Detect_PrefersManualSourceForTheSamePath()
    {
        using var temp = new TempDirectory();
        var installRoot = FakeInstallation.CreateUnity(temp.CreateDirectory("RimWorld"));

        var detector = new RimWorldInstallationDetector(CreateOptions(temp), new DiagnosticLog());
        var installations = detector.Detect([installRoot]);

        Assert.Single(installations);
        Assert.Equal(InstallationSource.Manual, installations[0].Source);
    }

    [Fact]
    public void Detect_FindsGogStyleFolder()
    {
        using var temp = new TempDirectory();
        var installRoot = FakeInstallation.CreateWaylith(temp.CreateDirectory("GOG Games", "RimWorld"));

        var detector = new RimWorldInstallationDetector(CreateOptions(temp), new DiagnosticLog());
        var installations = detector.Detect();

        Assert.Single(installations);
        Assert.Equal(InstallationSource.Gog, installations[0].Source);
        Assert.Equal(InstallationLayout.Waylith, installations[0].Layout);
        Assert.Equal(Path.GetFullPath(installRoot), installations[0].RootPath);
    }

    [Fact]
    public void Detect_UsesEnvironmentOverride()
    {
        using var temp = new TempDirectory();
        var installRoot = FakeInstallation.CreateUnity(temp.CreateDirectory("custom-install"));

        var detector = new RimWorldInstallationDetector(
            CreateOptions(temp, installRoot),
            new DiagnosticLog());

        var installations = detector.Detect();

        Assert.Single(installations);
        Assert.Equal(InstallationSource.EnvironmentVariable, installations[0].Source);
    }

    [Fact]
    public void Detect_SkipsAutoDetectedDirectoriesThatAreNotUsable()
    {
        using var temp = new TempDirectory();
        // A Steam library that contains an empty "RimWorld" folder must not be reported.
        temp.CreateDirectory(".steam", "steam", "steamapps", "common", "RimWorld");

        var detector = new RimWorldInstallationDetector(CreateOptions(temp), new DiagnosticLog());
        var installations = detector.Detect();

        Assert.Empty(installations);
    }

    [Fact]
    public void Detect_KeepsManualPathThatIsNotUsableSoTheUiCanExplainIt()
    {
        using var temp = new TempDirectory();
        var empty = temp.CreateDirectory("empty-modish-folder");

        var detector = new RimWorldInstallationDetector(CreateOptions(temp), new DiagnosticLog());
        var installations = detector.Detect([empty]);

        Assert.Single(installations);
        Assert.False(installations[0].IsUsable);
        Assert.True(installations[0].HasProblems);
    }

    [Fact]
    public void Detect_ReturnsEmptyWhenNothingIsInstalled()
    {
        using var temp = new TempDirectory();

        var detector = new RimWorldInstallationDetector(CreateOptions(temp), new DiagnosticLog());

        Assert.Empty(detector.Detect());
    }

    [Fact]
    public void Detect_SurvivesMalformedSteamLibraryFile()
    {
        using var temp = new TempDirectory();
        temp.CreateFile(".steam/steam/steamapps/libraryfolders.vdf", "\"libraryfolders\" { \"0\" {");

        var log = new DiagnosticLog();
        var detector = new RimWorldInstallationDetector(CreateOptions(temp), log);

        var installations = detector.Detect();

        Assert.Empty(installations);
        Assert.Contains(log.Snapshot(), entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void Detect_OrdersSupportedVersionsFirst()
    {
        using var temp = new TempDirectory();
        var old = FakeInstallation.CreateUnity(temp.CreateDirectory("RimWorld15"), "1.5.4104 rev845");
        var current = FakeInstallation.CreateWaylith(temp.CreateDirectory("RimWorld17"), "1.7.0");

        var detector = new RimWorldInstallationDetector(CreateOptions(temp), new DiagnosticLog());
        var installations = detector.Detect([old, current]);

        Assert.Equal(2, installations.Count);
        Assert.Equal(Path.GetFullPath(current), installations[0].RootPath);
    }

    [Fact]
    public async Task DetectAsync_ReturnsSameResultAsDetect()
    {
        using var temp = new TempDirectory();
        var installRoot = FakeInstallation.CreateUnity(temp.CreateDirectory("RimWorld"));

        var detector = new RimWorldInstallationDetector(CreateOptions(temp), new DiagnosticLog());
        var installations = await detector.DetectAsync([installRoot], TestContext.Current.CancellationToken);

        Assert.Single(installations);
        Assert.Equal(Path.GetFullPath(installRoot), installations[0].RootPath);
    }
}
