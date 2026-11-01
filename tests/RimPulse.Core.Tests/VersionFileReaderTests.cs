using RimPulse.Core.Game;
using RimPulse.Core.Installations;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class VersionFileReaderTests
{
    [Fact]
    public void ReadVersionText_ReturnsTrimmedContents()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("RimWorld");
        File.WriteAllText(Path.Combine(root, VersionFileReader.VersionFileName), "1.6.4851 rev1248\r\n");

        Assert.Equal("1.6.4851 rev1248", VersionFileReader.ReadVersionText(root));
    }

    [Fact]
    public void ReadVersionText_ReturnsNullWhenMissing()
    {
        using var temp = new TempDirectory();

        Assert.Null(VersionFileReader.ReadVersionText(temp.Root));
    }

    [Fact]
    public void ReadVersionText_ReturnsNullForEmptyFile()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Version.txt", "   \n");

        Assert.Null(VersionFileReader.ReadVersionText(temp.Root));
    }

    [Fact]
    public void ReadGameVersion_ParsesVersionFile()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Version.txt", "1.7.0");

        var version = VersionFileReader.ReadGameVersion(temp.Root);

        Assert.Equal(1, version.Major);
        Assert.Equal(7, version.Minor);
        Assert.True(version.IsSupported);
    }

    [Fact]
    public void ReadGameVersion_ReturnsUnknownForGarbage()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Version.txt", "unknown build");

        Assert.Equal(GameVersion.Unknown, VersionFileReader.ReadGameVersion(temp.Root));
    }
}
