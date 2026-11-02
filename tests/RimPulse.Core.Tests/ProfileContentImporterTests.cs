using RimPulse.Core.Configuration;
using RimPulse.Core.Runtime;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class ProfileContentImporterTests
{
    [Theory]
    [InlineData("custom.RWS", "Saves", "custom.rws")]
    [InlineData("quiet.rsc", "Scenarios", "quiet.rsc")]
    public void Import_CopiesToSelectedProfileWithoutChangingSource(string filename, string folder, string expectedName)
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("source/" + filename, "game data");
        var paths = new AppPaths(temp.Combine("config"), temp.Combine("state"));
        var profile = new DevelopmentProfileSettings();
        var result = ProfileContentImporter.Import(paths, profile, source);
        Assert.Equal(Path.Combine(PersistentDevelopmentProfile.DataDirectory(paths, profile), folder, expectedName), result);
        Assert.Equal("game data", File.ReadAllText(result));
        Assert.Equal("game data", File.ReadAllText(source));
    }

    [Fact]
    public void Import_RefusesOverwriteAndLeavesNoTemporaryFile()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("source/world.rws", "new");
        var paths = new AppPaths(temp.Combine("config"), temp.Combine("state"));
        var profile = new DevelopmentProfileSettings();
        var folder = Path.Combine(PersistentDevelopmentProfile.DataDirectory(paths, profile), "Saves");
        Directory.CreateDirectory(folder);
        var existing = Path.Combine(folder, "world.rws");
        File.WriteAllText(existing, "original");
        Assert.ThrowsAny<IOException>(() => ProfileContentImporter.Import(paths, profile, source));
        Assert.Equal("original", File.ReadAllText(existing));
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void Import_RejectsInvalidAndMissingSourcesBeforeCreatingProfileDirectory()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("config"), temp.Combine("state"));
        var profile = new DevelopmentProfileSettings();
        Assert.Throws<ArgumentException>(() => ProfileContentImporter.Import(paths, profile, temp.CreateFile("other.txt", "test")));
        Assert.Throws<FileNotFoundException>(() => ProfileContentImporter.Import(paths, profile, temp.Combine("missing.rws")));
        Assert.False(Directory.Exists(paths.StateDirectory));
    }

    [Fact]
    public void Import_RejectsInvalidProfileId()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("config"), temp.Combine("state"));
        var profile = new DevelopmentProfileSettings { Id = "../../outside" };
        Assert.Throws<ArgumentException>(() => ProfileContentImporter.Import(paths, profile, temp.CreateFile("world.rws", "data")));
        Assert.False(Directory.Exists(paths.StateDirectory));
    }

    [Fact]
    public void Import_RefusesRedirectedProfileDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var temp = new TempDirectory();
        var source = temp.CreateFile("source/world.rws", "new");
        var paths = new AppPaths(temp.Combine("config"), temp.Combine("state"));
        var profile = new DevelopmentProfileSettings();
        var data = PersistentDevelopmentProfile.DataDirectory(paths, profile);
        Directory.CreateDirectory(data);
        var outside = temp.CreateDirectory("outside");
        Directory.CreateSymbolicLink(Path.Combine(data, "Saves"), outside);
        Assert.Throws<IOException>(() => ProfileContentImporter.Import(paths, profile, source));
        Assert.Empty(Directory.GetFiles(outside));
    }
}
