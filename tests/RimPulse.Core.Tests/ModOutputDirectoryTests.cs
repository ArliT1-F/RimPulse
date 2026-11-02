using RimPulse.Core.Build;
using RimPulse.Core.Game;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class ModOutputDirectoryTests
{
    private static readonly GameVersion V16 = new(1, 6);
    private static readonly GameVersion V17 = new(1, 7);

    [Fact]
    public void Resolve_WithoutAVersionFolder_UsesTheModRootsAssembliesFolder()
    {
        using var temp = new TempDirectory();

        var output = ModOutputDirectoryResolver.Resolve(temp.Root, V16);

        Assert.Equal(temp.Combine("Assemblies"), output.Path);
        Assert.Null(output.VersionFolder);
        Assert.False(output.IsVersioned);
        Assert.Equal("the mod's Assemblies folder", output.Description);
    }

    [Fact]
    public void Resolve_WithAVersionFolder_WritesInsideIt()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6");

        var output = ModOutputDirectoryResolver.Resolve(temp.Root, V16);

        Assert.Equal(temp.Combine("1.6", "Assemblies"), output.Path);
        Assert.Equal("1.6", output.VersionFolder);
        Assert.True(output.IsVersioned);
        Assert.Equal("the mod's 1.6/Assemblies folder", output.Description);
    }

    [Fact]
    public void Resolve_WithAVersionFolder_FollowsTheSelectedInstallationNotTheHighestFolder()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6");
        temp.CreateDirectory("1.7");

        Assert.Equal(temp.Combine("1.6", "Assemblies"), ModOutputDirectoryResolver.Resolve(temp.Root, V16).Path);
        Assert.Equal(temp.Combine("1.7", "Assemblies"), ModOutputDirectoryResolver.Resolve(temp.Root, V17).Path);
    }

    [Fact]
    public void Resolve_IgnoresVersionFoldersOfOtherVersions()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.5");

        var output = ModOutputDirectoryResolver.Resolve(temp.Root, V16);

        Assert.Equal(temp.Combine("Assemblies"), output.Path);
        Assert.Null(output.VersionFolder);
    }

    [Fact]
    public void Resolve_WithAnUnknownVersion_UsesTheModRootsAssembliesFolder()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6");

        var output = ModOutputDirectoryResolver.Resolve(temp.Root, GameVersion.Unknown);

        Assert.Equal(temp.Combine("Assemblies"), output.Path);
        Assert.False(output.IsVersioned);
    }

    [Fact]
    public void Resolve_IgnoresAFileNamedLikeTheVersion()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("1.6", "not a folder");

        var output = ModOutputDirectoryResolver.Resolve(temp.Root, V16);

        Assert.Equal(temp.Combine("Assemblies"), output.Path);
    }

    [Fact]
    public void Resolve_PrefersTheVersionFoldersAssembliesEvenWhenTheRootHasOneToo()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("Assemblies");
        temp.CreateDirectory("1.6");

        var output = ModOutputDirectoryResolver.Resolve(temp.Root, V16);

        // 1.6 is the mod root RimWorld loads for 1.6, so the DLL belongs there and not in the top-level folder.
        Assert.Equal(temp.Combine("1.6", "Assemblies"), output.Path);
    }

    [Fact]
    public void Resolve_NormalizesTheModRootPath()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6");

        // A path pasted from a file manager or a shell arrives with quotes or padding around it.
        var output = ModOutputDirectoryResolver.Resolve($"  \"{temp.Root}\"  ", V16);

        Assert.Equal(temp.Combine("1.6", "Assemblies"), output.Path);
    }

    [Fact]
    public void Resolve_WithAMissingModDirectory_StillReturnsTheRootOutputFolder()
    {
        using var temp = new TempDirectory();
        var missing = temp.Combine("no-such-mod");

        var output = ModOutputDirectoryResolver.Resolve(missing, V16);

        Assert.Equal(Path.Combine(missing, "Assemblies"), output.Path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithoutAModDirectory_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => ModOutputDirectoryResolver.Resolve(path, V16));
    }

    [Fact]
    public void Resolve_UsesTheAssembliesFolderNameRimWorldLoads()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("1.6");

        var output = ModOutputDirectoryResolver.Resolve(temp.Root, V16);

        Assert.Equal("Assemblies", Path.GetFileName(output.Path));
        Assert.Equal(ModOutputDirectory.AssembliesFolderName, Path.GetFileName(output.Path));
    }
}
