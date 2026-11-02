using System.Xml.Linq;
using RimPulse.Core.Build;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class MsBuildPropertiesTests
{
    [Fact]
    public void Evaluate_ExpandsAPropertyWhenItIsDefined_NotWhenItIsUsed()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <Early>$(Late)</Early>
                <GameDir>/opt/RimWorld</GameDir>
                <Late>defined</Late>
                <After>$(GameDir)/Managed</After>
              </PropertyGroup>
            </Project>
            """);

        var properties = MsBuildPropertySet.Evaluate(project);

        // MSBuild expands a property when it is defined, so 'Early' cannot see a property defined after it.
        Assert.Equal("$(Late)", properties.Get("Early"));
        Assert.Equal("defined", properties.Get("Late"));
        Assert.Equal("/opt/RimWorld/Managed", properties.Get("After"));
        Assert.Null(properties.Get("NothingDefinesThis"));
    }

    [Fact]
    public void Evaluate_SeedsThePropertiesMSBuildProvides()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <AssemblyName>TestMod</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        var properties = MsBuildPropertySet.Evaluate(project);

        Assert.Equal("TestMod", properties.Get("MSBuildProjectName"));
        Assert.Equal("TestMod.csproj", properties.Get("MSBuildProjectFile"));
        Assert.Equal(Path.GetFullPath(Path.GetDirectoryName(project)!), properties.Get("MSBuildProjectDirectory"));

        // RimPulse compiles a development build, so conditions see Debug.
        Assert.Equal("Debug", properties.Get("Configuration"));
        Assert.Equal("AnyCPU", properties.Get("Platform"));
    }

    [Fact]
    public void Evaluate_GlobalPropertiesOverrideProjectAssignments()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <RimWorldDir>/wrong/install</RimWorldDir>
                <SelectedInstallPath>$(RimWorldDir)</SelectedInstallPath>
              </PropertyGroup>
            </Project>
            """);
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RimWorldDir"] = "/selected/install",
        };

        var properties = MsBuildPropertySet.Evaluate(project, globalProperties: globals);

        Assert.Equal("/selected/install", properties.Get("RimWorldDir"));
        Assert.Equal("/selected/install", properties.Get("SelectedInstallPath"));
    }

    [Fact]
    public void Evaluate_ReadsTheNearestDirectoryBuildProps()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("Directory.Build.props", """
            <Project>
              <PropertyGroup>
                <RimWorldManagedDir>$(MSBuildThisFileDirectory)game/Managed</RimWorldManagedDir>
              </PropertyGroup>
            </Project>
            """);
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <GameAssembly>$(RimWorldManagedDir)/Assembly-CSharp.dll</GameAssembly>
              </PropertyGroup>
            </Project>
            """);

        var properties = MsBuildPropertySet.Evaluate(project);

        var managed = temp.Root + Path.DirectorySeparatorChar + "game/Managed";
        Assert.Equal(managed, properties.Get("RimWorldManagedDir"));
        Assert.Equal(managed + "/Assembly-CSharp.dll", properties.Get("GameAssembly"));
    }

    [Fact]
    public void Evaluate_FollowsAnImportInDocumentOrder()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("build/Paths.props", """
            <Project>
              <PropertyGroup>
                <RimWorldManagedDir>/games/RimWorld/Managed</RimWorldManagedDir>
              </PropertyGroup>
            </Project>
            """);
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="../../build/Paths.props" />
              <PropertyGroup>
                <GameAssembly>$(RimWorldManagedDir)/Assembly-CSharp.dll</GameAssembly>
              </PropertyGroup>
            </Project>
            """);

        var properties = MsBuildPropertySet.Evaluate(project);

        Assert.Equal("/games/RimWorld/Managed", properties.Get("RimWorldManagedDir"));
        Assert.Equal("/games/RimWorld/Managed/Assembly-CSharp.dll", properties.Get("GameAssembly"));
    }

    [Fact]
    public void Evaluate_ReportsAnImportThatUsesAPropertyNothingDefines()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="$(NowhereDefined)/Paths.props" />
            </Project>
            """);

        var problems = new List<string>();
        MsBuildPropertySet.Evaluate(project, problems);

        Assert.Contains(problems, problem => problem.Contains("NowhereDefined", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ReportsAnImportedFileThatDoesNotExist()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="../Shared/Missing.props" />
            </Project>
            """);

        var problems = new List<string>();
        MsBuildPropertySet.Evaluate(project, problems);

        Assert.Contains(problems, problem => problem.Contains("Missing.props", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_BreaksAnImportCycle()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("a.props", """
            <Project>
              <Import Project="b.props" />
            </Project>
            """);
        temp.CreateFile("b.props", """
            <Project>
              <Import Project="a.props" />
            </Project>
            """);
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="../../a.props" />
            </Project>
            """);

        var problems = new List<string>();
        MsBuildPropertySet.Evaluate(project, problems);

        // The cycle is broken silently; nothing was reported as broken because nothing is.
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("'$(Configuration)' == 'Debug'", true)]
    [InlineData("'$(Configuration)' != 'Debug'", false)]
    [InlineData("'$(Configuration)' == 'debug'", true)]
    [InlineData("'$(Configuration)' == 'Release'", false)]
    [InlineData("'$(Configuration)' == 'Debug' and '$(Platform)' == 'AnyCPU'", true)]
    [InlineData("'$(Configuration)' == 'Release' or '$(Platform)' == 'AnyCPU'", true)]
    [InlineData("!('$(Configuration)' == 'Release')", true)]
    [InlineData("('$(Configuration)' == 'Debug')", true)]
    [InlineData("'$(Configuration)|$(Platform)' == 'Debug|AnyCPU'", true)]
    [InlineData("'$(NothingDefinesThis)' == ''", true)]
    [InlineData("'$(NothingDefinesThis)' == 'x'", false)]
    [InlineData("true", true)]
    [InlineData("'2' < '10'", true)]
    public void EvaluateCondition_ReadsTheConditionsAModProjectUses(string condition, bool expected)
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);

        var problems = new List<string>();
        var properties = MsBuildPropertySet.Evaluate(project, problems);
        var element = new XElement("PropertyGroup", new XAttribute("Condition", condition));

        Assert.Equal(expected, properties.EvaluateCondition(element, "TestMod.csproj", problems));
        Assert.Empty(problems);
    }

    [Fact]
    public void EvaluateCondition_SupportsExists()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);
        temp.CreateFile("Source/TestMod/local.props", "<Project />");

        var properties = MsBuildPropertySet.Evaluate(project);

        Assert.True(properties.EvaluateCondition(
            Condition($"Exists('$(MSBuildProjectDirectory)/local.props')"), "TestMod.csproj"));
        Assert.False(properties.EvaluateCondition(
            Condition("Exists('$(MSBuildProjectDirectory)/missing.props')"), "TestMod.csproj"));
        Assert.True(properties.EvaluateCondition(
            Condition("!Exists('$(MSBuildProjectDirectory)/missing.props')"), "TestMod.csproj"));
    }

    [Theory]
    [InlineData("'@(Compile)' != ''")]
    [InlineData("HasTrailingSlash('a') or UnsupportedFunction('b')")]
    [InlineData("'$(Configuration)' 'Debug'")]
    public void EvaluateCondition_ReportsAConditionItCannotEvaluate(string condition)
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);

        var problems = new List<string>();
        var properties = MsBuildPropertySet.Evaluate(project, problems);
        problems.Clear();

        Assert.False(properties.EvaluateCondition(Condition(condition), "TestMod.csproj", problems));
        Assert.Contains(problems, problem => problem.Contains("not evaluated by RimPulse", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_SelectsFirstMatchingChooseBranchAndFallsBackToOtherwise()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Choose>
                <When Condition="'$(Configuration)' == 'Release'"><PropertyGroup><Flavor>release</Flavor></PropertyGroup></When>
                <When Condition="'$(Configuration)' == 'Debug'"><PropertyGroup><Flavor>debug</Flavor></PropertyGroup></When>
                <Otherwise><PropertyGroup><Flavor>fallback</Flavor></PropertyGroup></Otherwise>
              </Choose>
            </Project>
            """);

        var problems = new List<string>();
        var properties = MsBuildPropertySet.Evaluate(project, problems);

        Assert.Equal("debug", properties.Get("Flavor"));
        Assert.Empty(problems);
    }

    [Fact]
    public void Evaluate_UsesOtherwiseWhenNoChooseConditionMatches()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Choose>
                <When Condition="'$(Configuration)' == 'Release'"><PropertyGroup><Flavor>release</Flavor></PropertyGroup></When>
                <Otherwise><PropertyGroup><Flavor>debug-fallback</Flavor></PropertyGroup></Otherwise>
              </Choose>
            </Project>
            """);

        var properties = MsBuildPropertySet.Evaluate(project);

        Assert.Equal("debug-fallback", properties.Get("Flavor"));
    }

    [Fact]
    public void Expand_KeepsAPropertyItCannotResolve()
    {
        using var temp = new TempDirectory();
        var project = temp.CreateFile("Source/TestMod/TestMod.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <Known>known</Known>
              </PropertyGroup>
            </Project>
            """);

        var properties = MsBuildPropertySet.Evaluate(project);

        Assert.False(properties.TryExpand("$(Known)/$(Unknown).dll", out var expanded, out var undefined));
        Assert.Equal("known/$(Unknown).dll", expanded);
        Assert.Equal("Unknown", undefined);

        Assert.True(properties.TryExpand("$(Known)/file.dll", out var resolved, out var none));
        Assert.Equal("known/file.dll", resolved);
        Assert.Null(none);
    }

    private static XElement Condition(string condition)
        => new("PropertyGroup", new XAttribute("Condition", condition));
}
