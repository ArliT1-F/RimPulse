using RimPulse.Core.Configuration;
using RimPulse.Core.Platform;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class AppPathsTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] values)
        => name => values.FirstOrDefault(entry => entry.Name == name).Value;

    [Fact]
    public void Resolve_Linux_UsesXdgOverrides()
    {
        var paths = AppPaths.Resolve(
            HostPlatform.Linux,
            "/home/rim",
            Environment(("XDG_CONFIG_HOME", "/custom/config"), ("XDG_STATE_HOME", "/custom/state")));

        Assert.Equal(Path.Combine("/custom/config", "RimPulse"), paths.ConfigurationDirectory);
        Assert.Equal(Path.Combine("/custom/state", "RimPulse"), paths.StateDirectory);
        Assert.Equal(Path.Combine("/custom/config", "RimPulse", "config.json"), paths.ConfigurationFilePath);
        Assert.Equal(Path.Combine("/custom/state", "RimPulse", "logs", "rimpulse.log"), paths.LogFilePath);
    }

    [Fact]
    public void Resolve_Linux_FallsBackToDotDirectories()
    {
        var paths = AppPaths.Resolve(HostPlatform.Linux, "/home/rim", _ => null);

        Assert.Equal(Path.Combine("/home/rim", ".config", "RimPulse"), paths.ConfigurationDirectory);
        Assert.Equal(Path.Combine("/home/rim", ".local", "state", "RimPulse"), paths.StateDirectory);
    }

    [Fact]
    public void Resolve_Windows_UsesAppDataDirectories()
    {
        var paths = AppPaths.Resolve(
            HostPlatform.Windows,
            "/users/rim",
            Environment(
                ("APPDATA", @"C:\Users\rim\AppData\Roaming"),
                ("LOCALAPPDATA", @"C:\Users\rim\AppData\Local")));

        Assert.Equal(Path.Combine(@"C:\Users\rim\AppData\Roaming", "RimPulse"), paths.ConfigurationDirectory);
        Assert.Equal(Path.Combine(@"C:\Users\rim\AppData\Local", "RimPulse"), paths.StateDirectory);
    }

    [Fact]
    public void Resolve_MacOS_UsesLibraryDirectories()
    {
        var paths = AppPaths.Resolve(HostPlatform.MacOS, "/Users/rim", _ => null);

        Assert.Equal(Path.Combine("/Users/rim", "Library", "Application Support", "RimPulse"), paths.ConfigurationDirectory);
        Assert.Equal(Path.Combine("/Users/rim", "Library", "Logs", "RimPulse"), paths.StateDirectory);
    }

    [Fact]
    public void EnsureCreated_CreatesBothDirectories()
    {
        var temp = Path.Combine(Path.GetTempPath(), "rimpulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(temp, "config"), Path.Combine(temp, "state"));

        try
        {
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.ConfigurationDirectory));
            Assert.True(Directory.Exists(paths.StateDirectory));
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    [Fact]
    public void HostPlatform_Detect_ReturnsAKnownPlatform()
    {
        var platform = HostPlatformExtensions.Detect();

        Assert.True(Enum.IsDefined(platform));
    }
}
