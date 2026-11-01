using RimPulse.Core.Build;
using Xunit;

namespace RimPulse.Core.Tests;

/// <summary>
/// The log file is what a mod author (or a bug report) has after a failed build, so these tests pin down that a
/// failure is explained inside it: the inputs, the reference set, the diagnostics and the error-code histogram.
/// </summary>
public sealed class BuildLogReportTests
{
    private static ModCompileUnit Unit(int sourceFileCount = 3)
    {
        var sources = Enumerable.Range(1, sourceFileCount).Select(index => $"/mod/Source/File{index}.cs").ToArray();

        return new ModCompileUnit(
            "/mod/Source/TestMod.csproj",
            "/mod/Source",
            "TestMod",
            "net472",
            sources,
            ["/mod/Source/lib/0Harmony.dll"],
            [],
            ["DEBUG"],
            "9.0",
            AllowUnsafe: false,
            NullableEnabled: false);
    }

    private static BuildDiagnostic Error(string id, string message, string file = "/mod/Source/Patch.cs", int line = 12, int column = 5)
        => new(BuildDiagnosticSeverity.Error, id, message, file, line, column);

    private static GameReferenceSet References()
        => new(["/game/Managed/Assembly-CSharp.dll", "/game/Managed/UnityEngine.CoreModule.dll"], []);

    [Fact]
    public void Describe_AFailedBuildRecordsTheInputsTheReferencesAndTheErrors()
    {
        var unit = Unit();
        var diagnostics = new[]
        {
            Error("CS0246", "The type or namespace name 'HarmonyLib' could not be found (are you missing a using directive or an assembly reference?)"),
            Error("CS0103", "The name 'PatchAll' does not exist in the current context", "/mod/Source/Mod.cs", 3, 9),
        };

        var compilation = new CompilationResult(false, null, null, diagnostics, [], TimeSpan.FromSeconds(2.5));
        var build = new ModBuildResult(
            false,
            [new BuildUnitResult(unit, compilation)],
            diagnostics,
            [],
            TimeSpan.FromSeconds(2.5));

        var summary = BuildLogReport.Describe(new BuildInputInspection([unit], []), References(), build, "/game/Managed");

        // The seconds are formatted with the current culture, so only the stable part is asserted.
        Assert.StartsWith("Build failed: 2 error(s), 0 warning(s) in ", summary.Message, StringComparison.Ordinal);
        Assert.NotNull(summary.Detail);

        var detail = summary.Detail!;
        Assert.Contains("Projects: 1 project(s), 3 source file(s)", detail, StringComparison.Ordinal);
        Assert.Contains("TestMod.csproj -> TestMod.dll, net472, 3 source(s), 1 reference(s), 0 project reference(s)", detail, StringComparison.Ordinal);
        Assert.Contains("defines: DEBUG | langversion: 9.0", detail, StringComparison.Ordinal);
        Assert.Contains("References: 2 assembly(ies) from '/game/Managed'", detail, StringComparison.Ordinal);
        Assert.Contains("Assembly-CSharp.dll, UnityEngine.CoreModule.dll", detail, StringComparison.Ordinal);
        Assert.Contains("Errors: 2", detail, StringComparison.Ordinal);
        Assert.Contains("Patch.cs:12:5 error CS0246", detail, StringComparison.Ordinal);
        Assert.Contains("Error codes: CS0103 ×1, CS0246 ×1", detail, StringComparison.Ordinal);
        Assert.Contains("Hint: CS0246", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_CapsALongDiagnosticListInsteadOfDumpingEveryRow()
    {
        var unit = Unit();
        var diagnostics = Enumerable.Range(1, 75)
            .Select(index => Error("CS0246", $"missing type number {index}"))
            .ToArray();

        var compilation = new CompilationResult(false, null, null, diagnostics, [], TimeSpan.FromSeconds(6.5));
        var build = new ModBuildResult(false, [new BuildUnitResult(unit, compilation)], diagnostics, [], TimeSpan.FromSeconds(6.5));

        var detail = BuildLogReport.Describe(new BuildInputInspection([unit], []), References(), build, null).Detail!;

        Assert.Contains("Errors: 75 (first 40)", detail, StringComparison.Ordinal);
        Assert.Contains("missing type number 40", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("missing type number 41", detail, StringComparison.Ordinal);
        Assert.Contains("… 35 more errors not shown", detail, StringComparison.Ordinal);
        Assert.Contains("Error codes: CS0246 ×75", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ExplainsAProblemWithoutACompile()
    {
        var problems = new[] { "No .csproj file was found, so there is no C# project to compile (XML-only mods have none)." };

        var summary = BuildLogReport.Describe(
            new BuildInputInspection([], problems),
            References(),
            build: null,
            referenceDirectory: null);

        Assert.StartsWith("No .csproj file was found", summary.Message, StringComparison.Ordinal);
        Assert.Contains("Projects: 0 (no .csproj was discovered)", summary.Detail!, StringComparison.Ordinal);
        Assert.Contains("Problems found before compiling: 1", summary.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ListsWarningsSeparatelyFromErrors()
    {
        var unit = Unit();
        var diagnostics = new[]
        {
            new BuildDiagnostic(BuildDiagnosticSeverity.Warning, "CS0169", "The field 'TestMod.Unused' is never used", "/mod/Source/Mod.cs", 7, 20),
        };

        var compilation = new CompilationResult(true, "/mod/Assemblies/TestMod.dll", null, diagnostics, [], TimeSpan.FromSeconds(1));
        var build = new ModBuildResult(true, [new BuildUnitResult(unit, compilation)], diagnostics, [], TimeSpan.FromSeconds(1));

        var summary = BuildLogReport.Describe(new BuildInputInspection([unit], []), References(), build, null);

        Assert.Contains("Warnings: 1", summary.Detail!, StringComparison.Ordinal);
        Assert.Contains("Mod.cs:7:20 warning CS0169", summary.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("Hint: CS0246", summary.Detail!, StringComparison.Ordinal);
    }
}
