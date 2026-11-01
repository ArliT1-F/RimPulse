using RimPulse.Core.Build;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class AssemblyInspectorTests
{
    private static string[] RuntimeReferences()
    {
        var wanted = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll" };
        var found = GameReferenceResolver.HostRuntimeAssemblies
            .Where(path => wanted.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();

        return found.Length > 0 ? found : [typeof(object).Assembly.Location];
    }

    [Fact]
    public void Inspect_ReadsNameVersionTypesAndReferences()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Source.cs", """
            namespace TestMod.Patches;

            public static class HarmonyPatchMarker { }

            internal static class InternalHelper { }
            """);
        var output = temp.Combine("TestMod.dll");

        var compilation = ModCompiler.Compile(
            new CompileRequest("TestMod", [source], RuntimeReferences(), OutputPath: output),
            TestContext.Current.CancellationToken);
        Assert.True(compilation.Succeeded, compilation.Summary);

        var inspection = AssemblyInspector.Inspect(output);

        Assert.Empty(inspection.Problems);
        Assert.Equal("TestMod", inspection.Name);
        Assert.NotNull(inspection.Version);
        Assert.False(inspection.TypesTruncated);

        Assert.Contains(inspection.Types, type => type.FullName == "TestMod.Patches.HarmonyPatchMarker" && type.IsPublic);
        Assert.Contains(inspection.Types, type => type.FullName == "TestMod.Patches.InternalHelper" && !type.IsPublic);
        Assert.DoesNotContain(inspection.Types, type => type.Name == "<Module>");
        Assert.NotEmpty(inspection.References);
        Assert.Contains("TestMod", inspection.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_ReadsARealAssemblyWithoutLoadingIt()
    {
        var path = typeof(AssemblyInspector).Assembly.Location;

        var inspection = AssemblyInspector.Inspect(path);

        Assert.Empty(inspection.Problems);
        Assert.Equal("RimPulse.Core", inspection.Name);
        Assert.Contains(inspection.Types, type => type.FullName == "RimPulse.Core.Build.AssemblyInspector");
        Assert.DoesNotContain(inspection.Types, type => type.Name == "<Module>");
    }

    [Theory]
    [InlineData("not a PE file at all")]
    [InlineData("MZ but not a real executable")]
    public void Inspect_ReportsFilesThatAreNotAssemblies(string content)
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("Fake.dll", content);

        var inspection = AssemblyInspector.Inspect(path);

        Assert.Null(inspection.Name);
        Assert.NotEmpty(inspection.Problems);
        Assert.Empty(inspection.Types);
    }

    [Fact]
    public void Inspect_ReportsMissingFiles()
    {
        using var temp = new TempDirectory();

        var inspection = AssemblyInspector.Inspect(temp.Combine("missing.dll"));

        Assert.Contains(inspection.Problems, problem => problem.Contains("does not exist", StringComparison.Ordinal));
        Assert.Contains("does not exist", inspection.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencesHarmony_IsTrueForHarmonyReferences()
    {
        var withHarmony = new AssemblyInspection("/x.dll", "X", "1.0.0.0", ["0Harmony", "Assembly-CSharp"], [], []);
        var withoutHarmony = new AssemblyInspection("/x.dll", "X", "1.0.0.0", ["Assembly-CSharp"], [], []);

        Assert.True(withHarmony.ReferencesHarmony);
        Assert.False(withoutHarmony.ReferencesHarmony);
    }
}
