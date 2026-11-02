using RimPulse.Core.Installations;
using RimPulse.Core.Platform;
using RimPulse.Core.Runtime;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class GameProcessLauncherTests
{
    [Theory]
    [InlineData(false, "RimWorldLinux")]
    [InlineData(true, "RimWorld")]
    public void ResolveExecutable_UsesVersionSpecificLinuxExecutable(bool waylith, string executable)
    {
        using var temp = new TempDirectory();
        var root = waylith ? FakeInstallation.CreateWaylith(temp.Root) : FakeInstallation.CreateUnity(temp.Root);
        var path = temp.CreateFile(executable);
        Assert.Equal(path, GameProcessLauncher.ResolveExecutable(InstallationProbe.Probe(root), HostPlatform.Linux));
    }

    [Fact]
    public void ResolveExecutable_RejectsMissingOrUnsupportedInstallations()
    {
        using var temp = new TempDirectory();
        FakeInstallation.CreateUnity(temp.Root);
        var installation = InstallationProbe.Probe(temp.Root);
        Assert.Throws<FileNotFoundException>(() => GameProcessLauncher.ResolveExecutable(installation, HostPlatform.Linux));
        Assert.Throws<PlatformNotSupportedException>(() => GameProcessLauncher.ResolveExecutable(installation, HostPlatform.Windows));
        File.Delete(Path.Combine(temp.Root, "Version.txt"));
        Assert.Throws<InvalidOperationException>(() => GameProcessLauncher.ResolveExecutable(InstallationProbe.Probe(temp.Root), HostPlatform.Linux));
    }

    [Fact]
    public void ResolveExecutable_RejectsMismatchedVersionAndLayout()
    {
        using var temp = new TempDirectory();
        FakeInstallation.CreateUnity(temp.Root, "1.7.0");
        temp.CreateFile("RimWorld");
        Assert.Throws<InvalidOperationException>(() => GameProcessLauncher.ResolveExecutable(InstallationProbe.Probe(temp.Root), HostPlatform.Linux));
    }

    [Fact]
    public void Start_RejectsInstallationChangedSinceSelection()
    {
        using var temp = new TempDirectory();
        FakeInstallation.CreateUnity(temp.Root);
        var selected = InstallationProbe.Probe(temp.Root);
        File.WriteAllText(Path.Combine(temp.Root, "Version.txt"), "1.6.4852 rev1249");
        Assert.Throws<InvalidOperationException>(() => GameProcessLauncher.Start(selected));
        File.WriteAllText(Path.Combine(temp.Root, "Version.txt"), "1.7.0");
        Assert.Throws<InvalidOperationException>(() => GameProcessLauncher.Start(selected));
    }

    [Fact]
    public async Task Start_TracksActualChildAndUsesInstallationWorkingDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        FakeInstallation.CreateUnity(temp.Root);
        var executable = temp.CreateFile("RimWorldLinux", "#!/bin/sh\npwd > working-directory.txt\nexit 7\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var process = GameProcessLauncher.Start(InstallationProbe.Probe(temp.Root));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, process.ExitCode);
        Assert.Equal(temp.Root, File.ReadAllText(Path.Combine(temp.Root, "working-directory.txt")).Trim());
    }

    [Fact]
    public void Start_ReportsNonExecutableFile()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        FakeInstallation.CreateUnity(temp.Root);
        var executable = temp.CreateFile("RimWorldLinux", "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => GameProcessLauncher.Start(InstallationProbe.Probe(temp.Root)));
    }
}
