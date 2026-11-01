using RimPulse.Core.Installations;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class InstallationProbeTests
{
    [Fact]
    public void Probe_RecognisesWaylithLayout()
    {
        using var temp = new TempDirectory();
        var root = FakeInstallation.CreateWaylith(temp.CreateDirectory("RimWorld17"), "1.7.0");

        var installation = InstallationProbe.Probe(root);

        Assert.Equal(InstallationLayout.Waylith, installation.Layout);
        Assert.Equal(Path.GetFullPath(root), installation.ManagedAssemblyDirectory);
        Assert.Equal(1, installation.Version.Major);
        Assert.Equal(7, installation.Version.Minor);
        Assert.Equal(GameVersionSource.VersionFile, installation.VersionSource);
        Assert.True(installation.IsUsable);
        Assert.True(installation.IsSupportedVersion);
        Assert.Empty(installation.MissingAssemblies);
        Assert.Equal(3, installation.PresentAssemblies.Count);
    }

    [Fact]
    public void Probe_RecognisesUnityManagedLayout()
    {
        using var temp = new TempDirectory();
        var root = FakeInstallation.CreateUnity(temp.CreateDirectory("RimWorld16"), "1.6.4851 rev1248");
        var expectedManagedDirectory = Path.Combine(root, "RimWorldLinux_Data", "Managed");

        var installation = InstallationProbe.Probe(root);

        Assert.Equal(InstallationLayout.UnityMono, installation.Layout);
        Assert.Equal(expectedManagedDirectory, installation.ManagedAssemblyDirectory);
        Assert.Equal("1.6.4851 rev1248", installation.VersionDisplay);
        Assert.True(installation.IsUsable);
        Assert.True(installation.IsSupportedVersion);
    }

    [Fact]
    public void Probe_AcceptsManagedDirectoryAndRecoversRoot()
    {
        using var temp = new TempDirectory();
        var root = FakeInstallation.CreateUnity(temp.CreateDirectory("RimWorld16"));
        var managedDirectory = Path.Combine(root, "RimWorldLinux_Data", "Managed");

        var installation = InstallationProbe.Probe(managedDirectory);

        Assert.Equal(Path.GetFullPath(root), installation.RootPath);
        Assert.Equal(managedDirectory, installation.ManagedAssemblyDirectory);
        Assert.Equal(InstallationLayout.UnityMono, installation.Layout);
        Assert.True(installation.IsUsable);
    }

    [Fact]
    public void Probe_ReportsMissingAssemblies()
    {
        using var temp = new TempDirectory();
        var root = FakeInstallation.CreateUnity(temp.CreateDirectory("RimWorld16"), includeAllAssemblies: false);

        var installation = InstallationProbe.Probe(root);

        Assert.False(installation.IsUsable);
        Assert.Equal(InstallationLayout.UnityMono, installation.Layout);
        Assert.Contains("UnityEngine.CoreModule.dll", installation.MissingAssemblies);
        Assert.True(installation.HasProblems);
    }

    [Fact]
    public void Probe_DoesNotConfirmVersionWithoutVersionFile()
    {
        using var temp = new TempDirectory();
        var root = FakeInstallation.CreateWaylith(temp.CreateDirectory("RimWorld17"), version: null);

        var installation = InstallationProbe.Probe(root);

        Assert.Equal(GameVersionSource.Unknown, installation.VersionSource);
        Assert.False(installation.Version.IsKnown);
        Assert.Equal("unconfirmed", installation.VersionDisplay);
        Assert.False(installation.IsSupportedVersion);
        Assert.Contains(installation.Problems, problem => problem.Contains("could not be confirmed", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_FlagsUnsupportedGameVersion()
    {
        using var temp = new TempDirectory();
        var root = FakeInstallation.CreateUnity(temp.CreateDirectory("RimWorld15"), "1.5.4104 rev845");

        var installation = InstallationProbe.Probe(root);

        Assert.True(installation.IsUsable);
        Assert.False(installation.IsSupportedVersion);
        Assert.Contains(installation.Problems, problem => problem.Contains("not a supported target version", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_ReportsMissingDirectory()
    {
        using var temp = new TempDirectory();
        var missing = temp.Combine("does", "not", "exist");

        var installation = InstallationProbe.Probe(missing);

        Assert.False(installation.RootExists);
        Assert.False(installation.IsUsable);
        Assert.Equal(InstallationLayout.Unknown, installation.Layout);
        Assert.True(installation.HasProblems);
    }

    [Fact]
    public void Probe_ReportsUnknownLayoutForEmptyDirectory()
    {
        using var temp = new TempDirectory();

        var installation = InstallationProbe.Probe(temp.Root);

        Assert.True(installation.RootExists);
        Assert.Equal(InstallationLayout.Unknown, installation.Layout);
        Assert.Null(installation.ManagedAssemblyDirectory);
        Assert.False(installation.IsUsable);
    }

    [Fact]
    public void NormalizePath_ExpandsTildeAndTrimsQuotes()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var normalized = InstallationProbe.NormalizePath("  \"~/RimWorld\"  ");

        Assert.Equal(Path.Combine(home, "RimWorld"), normalized);
    }

    [Fact]
    public void NormalizePath_ReturnsEmptyForBlankInput()
        => Assert.Equal(string.Empty, InstallationProbe.NormalizePath("   "));
}
