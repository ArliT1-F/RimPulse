namespace RimPulse.Core.Build;

/// <summary>
/// Everything a build run needs: the compile units in dependency order, the shared reference set and where
/// the assemblies should end up. <paramref name="OutputDirectory"/> null means "compile, but write nothing",
/// which validates a project without touching it.
/// </summary>
public sealed record ModBuildPlan(
    IReadOnlyList<ModCompileUnit> Units,
    IReadOnlyList<string> ReferencePaths,
    string? OutputDirectory);

/// <summary>The compile result of one project inside a build.</summary>
public sealed record BuildUnitResult(ModCompileUnit Unit, CompilationResult Compilation)
{
    public string AssemblyName => Unit.AssemblyName;
}

/// <summary>The result of building every project of one mod.</summary>
public sealed record ModBuildResult(
    bool Succeeded,
    IReadOnlyList<BuildUnitResult> Units,
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    IReadOnlyList<string> Problems,
    TimeSpan Elapsed)
{
    public int ErrorCount => Diagnostics.Count(diagnostic => diagnostic.Severity == BuildDiagnosticSeverity.Error);

    public int WarningCount => Diagnostics.Count(diagnostic => diagnostic.Severity == BuildDiagnosticSeverity.Warning);

    /// <summary>Output assemblies that were written, in build order.</summary>
    public IReadOnlyList<string> OutputPaths => Units
        .Select(unit => unit.Compilation.OutputPath)
        .OfType<string>()
        .ToArray();

    public string Summary
    {
        get
        {
            if (Units.Count == 0)
            {
                return Problems.FirstOrDefault() ?? "There was nothing to build.";
            }

            if (!Succeeded)
            {
                return $"Build failed: {ErrorCount} error(s), {WarningCount} warning(s) in {Elapsed.TotalSeconds:0.0}s.";
            }

            var names = string.Join(", ", Units.Select(unit => $"{unit.AssemblyName}.dll"));
            return OutputPaths.Count == 0
                ? $"Compiled {names} without writing files (validation only), {WarningCount} warning(s)."
                : $"Built {names} with {WarningCount} warning(s) in {Elapsed.TotalSeconds:0.0}s.";
        }
    }
}

/// <summary>
/// Compiles the projects of one mod in dependency order, adding each finished assembly to the reference set
/// of the projects that follow it. Nothing is written until a project compiles successfully.
/// </summary>
public static class ModBuilder
{
    public static ModBuildResult Build(ModBuildPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var problems = new List<string>(plan.Units.Count == 0
            ? ["No C# project was found to build."]
            : []);

        if (plan.Units.Count == 0)
        {
            return new ModBuildResult(false, [], [], problems, started.Elapsed);
        }

        var diagnostics = new List<BuildDiagnostic>();
        var results = new List<BuildUnitResult>(plan.Units.Count);
        var builtOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var succeeded = true;

        foreach (var unit in plan.Units)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var references = new List<string>(plan.ReferencePaths);
            references.AddRange(unit.ReferencePaths);
            references.AddRange(unit.ProjectReferences
                .Select(reference => builtOutputs.TryGetValue(reference, out var output) ? output : null)
                .OfType<string>());

            var outputPath = plan.OutputDirectory is null
                ? null
                : Path.Combine(plan.OutputDirectory, $"{unit.AssemblyName}.dll");

            var compilation = ModCompiler.Compile(
                new CompileRequest(
                    unit.AssemblyName,
                    unit.SourceFiles,
                    references,
                    unit.DefineConstants,
                    unit.LangVersion,
                    unit.AllowUnsafe,
                    unit.NullableEnabled,
                    outputPath),
                cancellationToken);

            results.Add(new BuildUnitResult(unit, compilation));
            diagnostics.AddRange(compilation.Diagnostics);

            if (!compilation.Succeeded)
            {
                succeeded = false;

                // Later projects reference this one; compiling them against a missing assembly would only
                // produce a second, misleading wave of errors.
                foreach (var problem in compilation.Problems)
                {
                    problems.Add($"{unit.AssemblyName}: {problem}");
                }

                if (compilation.Problems.Count == 0)
                {
                    problems.Add($"{unit.AssemblyName} did not compile; dependent projects were not built.");
                }

                break;
            }

            if (compilation.OutputPath is { } written)
            {
                builtOutputs[unit.ProjectFilePath] = written;
            }
        }

        var elapsed = started.Elapsed;
        return new ModBuildResult(succeeded, results, diagnostics, problems, elapsed);
    }
}
