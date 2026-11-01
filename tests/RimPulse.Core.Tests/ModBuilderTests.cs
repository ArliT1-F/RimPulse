using RimPulse.Core.Build;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class ModBuilderTests
{
    private static string[] RuntimeReferences()
    {
        var wanted = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll" };
        var found = GameReferenceResolver.HostRuntimeAssemblies
            .Where(path => wanted.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();

        return found.Length > 0 ? found : [typeof(object).Assembly.Location];
    }

    private static ModBuildResult Build(TempDirectory temp, string? outputDirectory)
    {
        var inputs = BuildInputDiscovery.Discover(temp.Root);
        return ModBuilder.Build(
            new ModBuildPlan(inputs.Units, RuntimeReferences(), outputDirectory),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Build_WritesTheAssemblyIntoTheModsAssembliesFolder()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>TestMod</AssemblyName></PropertyGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/Mod.cs", "namespace TestMod; public static class Mod { public const int Version = 1; }");

        var result = Build(temp, temp.Combine("Assemblies"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Problems));
        Assert.StartsWith("Built TestMod.dll with 0 warning(s)", result.Summary, StringComparison.Ordinal);
        var assembly = Assert.Single(result.OutputPaths);
        Assert.Equal(temp.Combine("Assemblies", "TestMod.dll"), assembly);
        Assert.True(File.Exists(assembly));
    }

    [Fact]
    public void Build_CompilesReferencedProjectsFirstAndLinksThem()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/Main/Main.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>Main</AssemblyName></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/Core/Core.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>Core</AssemblyName></PropertyGroup>
            </Project>
            """);
        temp.CreateFile("Source/Core/Shared.cs", "namespace Core; public static class Shared { public const string Name = \"core\"; }");
        temp.CreateFile("Source/Main/Main.cs", "namespace Main; public static class Entry { public const string Name = Core.Shared.Name; }");

        var result = Build(temp, temp.Combine("Assemblies"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Problems.Concat(result.Diagnostics.Select(d => d.Display))));
        Assert.Equal(new[] { "Core", "Main" }, result.Units.Select(unit => unit.AssemblyName));
        Assert.Equal(2, result.OutputPaths.Count);
    }

    [Fact]
    public void Build_StopsAfterTheFirstFailingProject()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/Main/Main.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>Main</AssemblyName></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Core\Core.csproj" />
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/Core/Core.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>Core</AssemblyName></PropertyGroup>
            </Project>
            """);
        temp.CreateFile("Source/Core/Broken.cs", "namespace Core; public static class Shared { public static int Value = \"no\"; }");
        temp.CreateFile("Source/Main/Main.cs", "namespace Main; public static class Entry { }");

        var result = Build(temp, temp.Combine("Assemblies"));

        Assert.False(result.Succeeded);
        Assert.Single(result.Units);
        Assert.Empty(result.OutputPaths);
        Assert.False(File.Exists(temp.Combine("Assemblies", "Core.dll")));
        Assert.Contains(result.Problems, problem => problem.Contains("Core", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_WithNoOutputDirectoryValidatesWithoutWriting()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>TestMod</AssemblyName></PropertyGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/Mod.cs", "namespace TestMod; public static class Mod { }");

        var result = Build(temp, outputDirectory: null);

        Assert.True(result.Succeeded);
        Assert.Empty(result.OutputPaths);
        Assert.False(Directory.Exists(temp.Combine("Assemblies")));
        Assert.Contains("validation only", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReportsAModWithoutAnyProject()
    {
        using var temp = new TempDirectory();

        var result = Build(temp, temp.Combine("Assemblies"));

        Assert.False(result.Succeeded);
        Assert.Empty(result.Units);
        Assert.Contains(result.Problems, problem => problem.Contains("No C# project", StringComparison.Ordinal));
    }
}
