using RimPulse.Core.Build;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

/// <summary>
/// A mod that declares its Harmony (or any other) dependency as a <c>PackageReference</c> could not be built
/// before: the item was ignored, so every Harmony type was reported as CS0246. These tests pin down that a
/// restored package is found in the packages folder and that an unrestorable one is explained.
/// </summary>
public sealed class NuGetPackageResolverTests
{
    private static void CreatePackage(TempDirectory temp, string id, string version, params string[] frameworkFolders)
    {
        foreach (var folder in frameworkFolders)
        {
            var kind = folder.Split('/')[0];
            var framework = folder.Split('/')[1];
            temp.CreateFile($"packages/{id.ToLowerInvariant()}/{version.ToLowerInvariant()}/{kind}/{framework}/{id}.dll", "MZ");
        }
    }

    [Fact]
    public void Resolve_ReadsARestoredPackageForTheProjectsOwnFramework()
    {
        using var temp = new TempDirectory();
        CreatePackage(temp, "Lib.Harmony", "2.3.3", "lib/net472", "lib/netstandard2.0");

        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", "2.3.3", "net472", temp.Combine("packages"));

        Assert.True(resolution.Resolved);
        Assert.Null(resolution.Problem);
        var assembly = Assert.Single(resolution.AssemblyPaths);
        Assert.EndsWith(Path.Combine("net472", "Lib.Harmony.dll"), assembly, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsAPackageThatHasNotBeenRestored()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("packages");

        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", "2.3.3", "net472", temp.Combine("packages"));

        Assert.False(resolution.Resolved);
        Assert.Contains("is not in the NuGet cache", resolution.Problem, StringComparison.Ordinal);
        Assert.Contains("HintPath", resolution.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsAFloatingVersionInsteadOfGuessing()
    {
        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", "2.*", "net472", packagesFolder: "/nonexistent");

        Assert.False(resolution.Resolved);
        Assert.Contains("floating version", resolution.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsAVersionRangeInsteadOfGuessing()
    {
        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", "[2.0,3.0)", "net472", packagesFolder: "/nonexistent");

        Assert.False(resolution.Resolved);
        Assert.Contains("floating version", resolution.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReportsAMissingVersion()
    {
        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", version: null, "net472", packagesFolder: "/nonexistent");

        Assert.False(resolution.Resolved);
        Assert.Contains("declares no version", resolution.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PrefersTheCompileTimeRefAssembliesOverLib()
    {
        using var temp = new TempDirectory();
        CreatePackage(temp, "Lib.Harmony", "2.3.3", "lib/net472", "ref/net472");

        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", "2.3.3", "net472", temp.Combine("packages"));

        Assert.True(resolution.Resolved);
        Assert.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", Assert.Single(resolution.AssemblyPaths), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PicksTheNearestLowerFrameworkForANet472Project()
    {
        using var temp = new TempDirectory();
        CreatePackage(temp, "Lib.Harmony", "2.3.3", "lib/net45", "lib/net48");

        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", "2.3.3", "net472", temp.Combine("packages"));

        Assert.True(resolution.Resolved);
        Assert.EndsWith(Path.Combine("net45", "Lib.Harmony.dll"), Assert.Single(resolution.AssemblyPaths), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DoesNotOfferANetFrameworkFolderToANet10Project()
    {
        using var temp = new TempDirectory();
        CreatePackage(temp, "Lib.Harmony", "2.3.3", "lib/net472");

        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", "2.3.3", "net10.0", temp.Combine("packages"));

        // netstandard2.0 is the compatible fallback list's first entry; nothing here matches, so this must fail
        // rather than silently compile a .NET 10 mod against a .NET Framework assembly.
        Assert.False(resolution.Resolved);
        Assert.Contains("none is compatible", resolution.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PicksTheNearestLowerModernFrameworkForANet10Project()
    {
        using var temp = new TempDirectory();
        CreatePackage(temp, "Lib.Harmony", "2.3.3", "lib/net8.0", "lib/netstandard2.0");

        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", "2.3.3", "net10.0", temp.Combine("packages"));

        Assert.True(resolution.Resolved);
        Assert.EndsWith(Path.Combine("net8.0", "Lib.Harmony.dll"), Assert.Single(resolution.AssemblyPaths), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AcceptsASingleFolderWhenTheProjectDeclaresNoFramework()
    {
        using var temp = new TempDirectory();
        CreatePackage(temp, "Lib.Harmony", "2.3.3", "lib/net472");

        var resolution = NuGetPackageResolver.Resolve("Lib.Harmony", "2.3.3", targetFramework: null, temp.Combine("packages"));

        Assert.True(resolution.Resolved);
    }

    [Fact]
    public void Resolve_ReportsAPackageWithoutAnyFrameworkFolder()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("packages/test.build/1.0.0/README.md", "no assemblies");

        var resolution = NuGetPackageResolver.Resolve("Test.Build", "1.0.0", "net472", temp.Combine("packages"));

        Assert.False(resolution.Resolved);
        Assert.Contains("ships no lib/ or ref/ folder", resolution.Problem, StringComparison.Ordinal);
    }
}
