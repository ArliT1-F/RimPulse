using RimPulse.Core.Build;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class BuildInputDiscoveryTests
{
    [Fact]
    public void Discover_FindsSdkStyleProjectAndItsImplicitSources()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net472</TargetFramework>
                <AssemblyName>TestMod</AssemblyName>
                <LangVersion>9.0</LangVersion>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <DefineConstants>DEBUG;CUSTOM</DefineConstants>
              </PropertyGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/HarmonyPatches/Patch.cs", "// patch");
        temp.CreateFile("Source/TestMod/Mod.cs", "// mod");
        temp.CreateFile("Source/TestMod/bin/Debug/ignored.cs", "// ignored");
        temp.CreateFile("Source/TestMod/obj/ignored.cs", "// ignored");

        var result = BuildInputDiscovery.Discover(temp.Root);

        Assert.Empty(result.Problems);
        var unit = Assert.Single(result.Units);
        Assert.Equal("TestMod", unit.AssemblyName);
        Assert.Equal("net472", unit.TargetFramework);
        Assert.Equal("9.0", unit.LangVersion);
        Assert.True(unit.AllowUnsafe);
        Assert.False(unit.NullableEnabled);
        Assert.Equal(new[] { "DEBUG", "CUSTOM" }, unit.DefineConstants);
        Assert.Equal(2, unit.SourceFiles.Count);
        Assert.All(unit.SourceFiles, file => Assert.DoesNotContain($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", file, StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_ReportsAPackageReferenceItCannotResolve()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net472</TargetFramework>
                <AssemblyName>TestMod</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Lib.Harmony" Version="2.*" />
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/Mod.cs", "// mod");

        var result = BuildInputDiscovery.Discover(temp.Root);

        var unit = Assert.Single(result.Units);
        Assert.Empty(unit.ReferencePaths);
        Assert.Contains(
            result.Problems,
            problem => problem.Contains("Lib.Harmony 2.*", StringComparison.Ordinal)
                       && problem.Contains("floating version", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_ReadsAPackageReferenceThatIsAlreadyRestored()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net472</TargetFramework>
                <AssemblyName>TestMod</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Lib.Harmony" Version="2.3.3" />
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/Mod.cs", "// mod");

        var packages = temp.CreateDirectory("packages");
        temp.CreateFile("packages/lib.harmony/2.3.3/lib/net472/Lib.Harmony.dll", "MZ");

        var folder = NuGetPackageResolver.GlobalPackagesFolder;
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);

        try
        {
            var result = BuildInputDiscovery.Discover(temp.Root);
            var unit = Assert.Single(result.Units);

            Assert.Contains(
                unit.ReferencePaths,
                path => path.EndsWith("Lib.Harmony.dll", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", folder);
        }
    }

    [Fact]
    public void Discover_ReadsLegacyCompileItemsAndHintPaths()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <AssemblyName>TestMod</AssemblyName>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Main.cs" />
                <Compile Include="Utility\Helper.cs" />
                <Compile Include="**\*.cs" />
                <Compile Include="Generated/*.g.cs" />
                <Reference Include="Assembly-CSharp">
                  <HintPath>..\..\RimWorld\Managed\Assembly-CSharp.dll</HintPath>
                </Reference>
                <Reference Include="System.Xml" />
                <Reference Include="Missing">
                  <HintPath>..\..\nowhere\Missing.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/Main.cs", "// main");
        temp.CreateFile("Source/TestMod/Utility/Helper.cs", "// helper");
        temp.CreateFile("Source/TestMod/Extra.cs", "// extra");
        temp.CreateFile("RimWorld/Managed/Assembly-CSharp.dll", string.Empty);

        var result = BuildInputDiscovery.Discover(temp.Root);

        var unit = Assert.Single(result.Units);
        Assert.True(unit.NullableEnabled);
        Assert.Equal(3, unit.SourceFiles.Count);
        Assert.Contains(unit.ReferencePaths, path => path.EndsWith("Assembly-CSharp.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(unit.ReferencePaths, path => path.EndsWith("Missing.dll", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Contains("Missing.dll", StringComparison.Ordinal));
        Assert.Contains(result.Problems, problem => problem.Contains("wildcard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Discover_ReportsModsWithoutAnyProject()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("About/About.xml", "<ModMetaData />");

        var result = BuildInputDiscovery.Discover(temp.Root);

        Assert.False(result.HasUnits);
        Assert.Contains(result.Problems, problem => problem.Contains(".csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_OrdersReferencedProjectsFirst()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/Main/Main.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/Core/Core.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        temp.CreateFile("Source/Main/Main.cs", "// main");
        temp.CreateFile("Source/Core/Core.cs", "// core");

        var result = BuildInputDiscovery.Discover(temp.Root);

        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Units.Count);
        Assert.Equal("Core", result.Units[0].AssemblyName);
        Assert.Equal("Main", result.Units[1].AssemblyName);
    }

    [Fact]
    public void Discover_ReportsReferenceToAProjectThatIsNotPresent()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/Main/Main.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Missing\Missing.csproj" />
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/Main/Main.cs", "// main");

        var result = BuildInputDiscovery.Discover(temp.Root);

        Assert.Contains(result.Problems, problem => problem.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_ReportsMalformedProjectFiles()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/Broken/Broken.csproj", "<Project><PropertyGroup></Project>");

        var result = BuildInputDiscovery.Discover(temp.Root);

        Assert.Empty(result.Units);
        Assert.Contains(result.Problems, problem => problem.Contains("not well-formed", StringComparison.Ordinal));
    }

    [Fact]
    public void Discover_HandlesMissingDirectory()
    {
        using var temp = new TempDirectory();

        var result = BuildInputDiscovery.Discover(temp.Combine("nowhere"));

        Assert.False(result.HasUnits);
        Assert.Contains(result.Problems, problem => problem.Contains("does not exist", StringComparison.Ordinal));
    }
}
