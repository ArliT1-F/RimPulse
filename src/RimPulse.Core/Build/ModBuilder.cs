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
        var reportedNotes = new HashSet<string>(StringComparer.Ordinal);
        var succeeded = true;

        foreach (var unit in plan.Units)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var references = MergeReferences(plan, unit, builtOutputs, problems, reportedNotes);

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

                // CS0436 means the project's own sources re-declare a type the reference set already provides —
                // a hand-written stand-in for the game's API compiled next to the real thing. The compiler then
                // reports every use of it as ambiguous, which buries the cause under hundreds of errors.
                if (compilation.Diagnostics.Any(diagnostic => diagnostic.Id == DuplicateTypeWarning))
                {
                    problems.Add(
                        $"{unit.AssemblyName}: the project defines its own copies of types the game already " +
                        "provides (CS0436), so the two sets cannot be used side by side. Exclude the stand-in " +
                        "sources from this project, or stop compiling it against the game assemblies.");
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

    /// <summary>
    /// Merges the shared installation references with the ones a single project declares.
    /// </summary>
    /// <remarks>
    /// The same simple assembly name may only appear once in a compilation: a second copy makes Roslyn report
    /// CS1703 ("multiple assemblies with equivalent identity have been imported") and turns every type they
    /// share into an ambiguous reference, which buries the real errors. So a duplicate is resolved explicitly
    /// and reported, never silently, because which copy wins changes what the mod compiles against:
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///       <b>Platform assemblies</b> (the game, Unity, the framework) always come from the installation — a
    ///       mod's copy of <c>Assembly-CSharp.dll</c> is the wrong game's API, not a newer one.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///       <b>Any other assembly</b> (Harmony, a shared library) keeps the mod's own copy, which is the version
    ///       its author builds against and ships next to the mod.
    ///     </description>
    ///   </item>
    /// </list>
    /// </remarks>
    private static List<string> MergeReferences(
        ModBuildPlan plan,
        ModCompileUnit unit,
        IReadOnlyDictionary<string, string> builtOutputs,
        List<string> problems,
        HashSet<string> reportedNotes)
    {
        var references = new List<string>(plan.ReferencePaths.Count + unit.ReferencePaths.Count);
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fromInstallation = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in plan.ReferencePaths)
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0 || !positions.TryAdd(name, references.Count))
            {
                continue;
            }

            references.Add(path);
            fromInstallation.Add(name);
        }

        foreach (var path in unit.ReferencePaths)
        {
            var name = Path.GetFileName(path);

            if (name.Length == 0)
            {
                references.Add(path);
                continue;
            }

            if (!positions.TryGetValue(name, out var position))
            {
                positions[name] = references.Count;
                references.Add(path);
                continue;
            }

            if (fromInstallation.Contains(name) && IsPlatformAssembly(name))
            {
                Report(
                    reportedNotes,
                    problems,
                    $"{unit.AssemblyName}: '{name}' from the project is not referenced; the installation's copy is used.");
                continue;
            }

            if (!fromInstallation.Contains(name))
            {
                // The project lists the same assembly twice. The first copy stays, which is deterministic and
                // matches how MSBuild keeps its items in order.
                Report(
                    reportedNotes,
                    problems,
                    $"{unit.AssemblyName}: '{name}' is referenced twice by the project; '{references[position]}' is used.");
                continue;
            }

            Report(
                reportedNotes,
                problems,
                $"{unit.AssemblyName}: '{name}' from the project replaces the installation's copy.");
            fromInstallation.Remove(name);
            references[position] = path;
        }

        foreach (var reference in unit.ProjectReferences)
        {
            if (!builtOutputs.TryGetValue(reference, out var output))
            {
                continue;
            }

            var name = Path.GetFileName(output);
            if (positions.TryAdd(name, references.Count))
            {
                references.Add(output);
            }
        }

        return references;
    }

    private static void Report(HashSet<string> reportedNotes, List<string> problems, string note)
    {
        if (reportedNotes.Add(note))
        {
            problems.Add(note);
        }
    }

    /// <summary>Roslyn's "type conflicts with the imported type" warning, reported at every use of the type.</summary>
    private const string DuplicateTypeWarning = "CS0436";

    /// <summary>
    /// True for assemblies the game or the framework owns, so a project-local copy must not shadow them.
    /// </summary>
    private static bool IsPlatformAssembly(string fileName)
        => PlatformAssemblyPrefixes.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] PlatformAssemblyPrefixes =
    [
        "Assembly-CSharp",
        "UnityEngine",
        "Unity.",
        "mscorlib",
        "System",
        "netstandard",
        "Mono.",
        "I18N",
        "Facades",
    ];
}
