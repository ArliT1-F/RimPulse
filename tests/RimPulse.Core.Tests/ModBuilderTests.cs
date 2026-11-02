using RimPulse.Core.Build;
using RimPulse.Core.Game;
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

    /// <summary>
    /// Compiles a small library with RimPulse's own compiler, so a reference-duplication test has two real
    /// assemblies with the same simple name instead of a file that only looks like one.
    /// </summary>
    private static string CreateLibrary(TempDirectory temp, string assemblyName, string directory)
    {
        Directory.CreateDirectory(directory);

        // An assembly name such as "Assembly-CSharp" is not a legal C# namespace; the fixture only needs a real
        // assembly with that file name, so the namespace is sanitised.
        var identifier = new string(assemblyName.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());
        if (identifier.Length == 0 || char.IsAsciiDigit(identifier[0]))
        {
            identifier = "_" + identifier;
        }

        var source = temp.CreateFile(
            $"libsrc/{assemblyName}/{assemblyName}.cs",
            $"namespace {identifier}; public static class Marker {{ public const int Value = 1; }}");
        var output = Path.Combine(directory, $"{assemblyName}.dll");

        var result = ModCompiler.Compile(
            new CompileRequest(assemblyName, [source], RuntimeReferences(), OutputPath: output),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Problems));
        return output;
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
    public void Build_WritesTheAssemblyIntoTheVersionedAssembliesFolderWhenTheModHasOne()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><AssemblyName>TestMod</AssemblyName></PropertyGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/Mod.cs", "namespace TestMod; public static class Mod { public const int Version = 1; }");

        // A mod that ships a version folder is loaded from it, so the DLL has to be written inside it too.
        temp.CreateDirectory("1.6");
        var output = ModOutputDirectoryResolver.Resolve(temp.Root, new GameVersion(1, 6));

        var result = Build(temp, output.Path);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Problems));
        var assembly = Assert.Single(result.OutputPaths);
        Assert.Equal(temp.Combine("1.6", "Assemblies", "TestMod.dll"), assembly);
        Assert.True(File.Exists(assembly));
        Assert.False(File.Exists(temp.Combine("Assemblies", "TestMod.dll")));
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

    [Fact]
    public void Build_LetsAProjectReferenceReplaceTheInstallationsCopyOfALibrary()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net472</TargetFramework>
                <AssemblyName>TestMod</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Shared">
                  <HintPath>lib/Shared.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/Mod.cs", "namespace TestMod; public static class Mod { }");

        var installed = CreateLibrary(temp, "Shared", temp.Combine("Game", "Managed"));
        var fromProject = CreateLibrary(temp, "Shared", temp.Combine("Source", "TestMod", "lib"));

        var inputs = BuildInputDiscovery.Discover(temp.Root);
        var result = ModBuilder.Build(
            new ModBuildPlan(
                inputs.Units,
                [.. RuntimeReferences(), installed],
                temp.Combine("Assemblies")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Problems));
        Assert.Equal(Path.GetFullPath(fromProject), Path.GetFullPath(Assert.Single(inputs.Units[0].ReferencePaths)));
        Assert.Contains(
            result.Problems,
            problem => problem.Contains("Shared.dll", StringComparison.Ordinal)
                       && problem.Contains("replaces the installation's copy", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_KeepsTheInstallationsCopyOfAGameAssembly()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net472</TargetFramework>
                <AssemblyName>TestMod</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Assembly-CSharp">
                  <HintPath>lib/Assembly-CSharp.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/Mod.cs", "namespace TestMod; public static class Mod { }");

        var installed = CreateLibrary(temp, "Assembly-CSharp", temp.Combine("Game", "Managed"));
        CreateLibrary(temp, "Assembly-CSharp", temp.Combine("Source", "TestMod", "lib"));

        var inputs = BuildInputDiscovery.Discover(temp.Root);
        var result = ModBuilder.Build(
            new ModBuildPlan(
                inputs.Units,
                [.. RuntimeReferences(), installed],
                temp.Combine("Assemblies")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Problems));
        Assert.Contains(
            result.Problems,
            problem => problem.Contains("Assembly-CSharp.dll", StringComparison.Ordinal)
                       && problem.Contains("the installation's copy is used", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ReportsAnAssemblyProvidedTwiceByTheSameProject()
    {
        using var temp = new TempDirectory();
        var first = CreateLibrary(temp, "Shared", temp.Combine("Source", "TestMod", "lib"));
        var second = CreateLibrary(temp, "Shared", temp.Combine("Source", "TestMod", "lib2"));

        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net472</TargetFramework>
                <AssemblyName>TestMod</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Shared">
                  <HintPath>lib/Shared.dll</HintPath>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        temp.CreateFile("Source/TestMod/Mod.cs", "namespace TestMod; public static class Mod { }");

        var inputs = BuildInputDiscovery.Discover(temp.Root);
        var unit = inputs.Units[0];
        var duplicate = new ModCompileUnit(
            unit.ProjectFilePath,
            unit.ProjectDirectory,
            unit.AssemblyName,
            unit.TargetFramework,
            unit.SourceFiles,
            [.. unit.ReferencePaths, second],
            unit.ProjectReferences,
            unit.DefineConstants,
            unit.LangVersion,
            unit.AllowUnsafe,
            unit.NullableEnabled);

        var result = ModBuilder.Build(
            new ModBuildPlan([duplicate], RuntimeReferences(), temp.Combine("Assemblies")),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Problems));
        Assert.Contains(
            result.Problems,
            problem => problem.Contains("Shared.dll", StringComparison.Ordinal)
                       && problem.Contains("is referenced twice by the project", StringComparison.Ordinal));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void Build_ExplainsAProjectThatDeclaresTypesTheGameAlreadyProvides()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net472</TargetFramework>
                <AssemblyName>TestMod</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        // A hand-written stand-in for the game's API, compiled next to a real assembly that already has the type.
        temp.CreateFile("Source/TestMod/VerseDoubles.cs", """
            namespace Verse;
            public static class Translator
            {
                public static string Translate(string key) => key;
            }
            """);
        temp.CreateFile("Source/TestMod/Usage.cs", """
            namespace TestMod;
            public static class Usage
            {
                public static string Call() => Verse.Translator.Translate("key");
                public static int Broken = "not an int";
            }
            """);

        var gameSource = temp.CreateFile("libsrc/Game/Game.cs", """
            namespace Verse;
            public static class Translator
            {
                public static string Translate(string key) => key;
            }
            """);
        var libraryDirectory = temp.Combine("Game", "Managed");
        Directory.CreateDirectory(libraryDirectory);
        var gameLibrary = Path.Combine(libraryDirectory, "Game.dll");
        var library = ModCompiler.Compile(
            new CompileRequest("Game", [gameSource], RuntimeReferences(), OutputPath: gameLibrary),
            TestContext.Current.CancellationToken);
        Assert.True(library.Succeeded, string.Join(Environment.NewLine, library.Problems));

        var inputs = BuildInputDiscovery.Discover(temp.Root);
        var result = ModBuilder.Build(
            new ModBuildPlan(inputs.Units, [.. RuntimeReferences(), gameLibrary], null),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "CS0436");
        Assert.Contains(
            result.Problems,
            problem => problem.Contains("copies of types the game already provides", StringComparison.Ordinal));
    }
}
