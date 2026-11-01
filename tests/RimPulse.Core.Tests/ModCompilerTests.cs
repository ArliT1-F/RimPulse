using RimPulse.Core.Build;
using RimPulse.Core.Tests.TestSupport;
using Xunit;

namespace RimPulse.Core.Tests;

public sealed class ModCompilerTests
{
    /// <summary>The framework assemblies the test process itself runs on, used as compile references.</summary>
    private static string[] RuntimeReferences()
    {
        var wanted = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll", "System.Console.dll" };
        var found = GameReferenceResolver.HostRuntimeAssemblies
            .Where(path => wanted.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();

        return found.Length > 0 ? found : [typeof(object).Assembly.Location];
    }

    [Fact]
    public void Compile_ProducesAnAssemblyAndNoErrors()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Source.cs", """
            namespace TestMod;

            public static class Greeter
            {
                public static string Greet(string name) => $"Hello, {name}!";
            }
            """);
        var output = temp.Combine("Assemblies", "TestMod.dll");

        var result = ModCompiler.Compile(new CompileRequest(
            "TestMod",
            [source],
            RuntimeReferences(),
            OutputPath: output));

        Assert.Empty(result.Problems);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Display)));
        Assert.Equal(0, result.ErrorCount);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.ChangeExtension(output, ".pdb")));
        Assert.Equal(output, result.OutputPath);
    }

    [Fact]
    public void Compile_ReportsErrorsWithFileLineAndColumnAndWritesNothing()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Broken.cs", """
            namespace TestMod;

            public static class Broken
            {
                public static int Value() => "not an int";
            }
            """);
        var output = temp.Combine("Assemblies", "TestMod.dll");

        var result = ModCompiler.Compile(new CompileRequest(
            "TestMod",
            [source],
            RuntimeReferences(),
            OutputPath: output));

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(output));

        var error = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Severity == BuildDiagnosticSeverity.Error);
        Assert.Equal(source, error.FilePath);
        Assert.Equal(5, error.Line);
        Assert.True(error.Column > 0);
        Assert.Contains("error CS", error.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_UsesTheProjectsLanguageVersionAndDefines()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Conditional.cs", """
            namespace TestMod;

            public static class Conditional
            {
            #if CUSTOM_SYMBOL
                public static string Mode => "custom";
            #else
                public static string Mode => "default";
            #endif
            }
            """);

        var result = ModCompiler.Compile(new CompileRequest(
            "TestMod",
            [source],
            RuntimeReferences(),
            DefineConstants: ["CUSTOM_SYMBOL"]));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Display)));
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public void Compile_UsesTheDeclaredCSharpVersion()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Modern.cs", """
            namespace TestMod;

            public static class Modern
            {
                public static string Join(string[] values) => string.Join(", ", [.. values, "extra"]);
            }
            """);

        var tooOld = ModCompiler.Compile(new CompileRequest("TestMod", [source], RuntimeReferences(), LangVersion: "7.3"));

        Assert.False(tooOld.Succeeded);
        Assert.Contains(tooOld.Diagnostics, diagnostic => diagnostic.Severity == BuildDiagnosticSeverity.Error);
    }

    [Fact]
    public void Compile_ReportsMissingSourcesAndUnusableReferences()
    {
        using var temp = new TempDirectory();
        var missingSource = ModCompiler.Compile(new CompileRequest("TestMod", [temp.Combine("nope.cs")], RuntimeReferences()));
        Assert.False(missingSource.Succeeded);
        Assert.Contains(missingSource.Problems, problem => problem.Contains("does not exist", StringComparison.Ordinal));

        var source = temp.CreateFile("Source.cs", "namespace TestMod; public class Thing { }");
        var notAnAssembly = temp.CreateFile("Fake.dll", "this is not a PE file");
        var badReference = ModCompiler.Compile(new CompileRequest("TestMod", [source], [notAnAssembly]));
        Assert.False(badReference.Succeeded);
        Assert.Contains(badReference.Problems, problem => problem.Contains("could not be used as a reference", StringComparison.Ordinal));
        Assert.Contains(badReference.Problems, problem => problem.Contains("No reference assemblies", StringComparison.Ordinal));
    }

    [Fact]
    public void Compile_RejectsAnEmptyAssemblyName()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateFile("Source.cs", "namespace TestMod; public class Thing { }");

        var result = ModCompiler.Compile(new CompileRequest("  ", [source], RuntimeReferences()));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, problem => problem.Contains("assembly name", StringComparison.Ordinal));
    }
}
